using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Scheduler.Workers;

namespace Test.Unit.Hosting;

/// <summary>
/// Settlement of a claimed work batch (D-026), the step that decides whether a row is deleted, retried, parked or
/// handed back. Its ordering is the error-prone part of the outbox: settle with the stopping token and a shutdown
/// after a successful send re-publishes the batch; release on the wrong attempt count and a poison row never
/// parks. Asserted here against a recording repository.
/// Pure-unit tier: fakes only, no database, host, or network.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class WorkSettlementTests
{
    private const int MaxAttempts = 10;

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies every outcome goes to the right statement: completions in one delete, a permanent failure and a
    /// failure on the last attempt to dead-letter, an earlier transient failure to release with its attempt.
    /// </summary>
    [TestMethod]
    public async Task Given_MixedOutcomes_When_Settled_Then_EachRowGetsItsStatement()
    {
        var done1 = NewWork(attempts: 1);
        var done2 = NewWork(attempts: 3);
        var permanent = NewWork(attempts: 1);
        var lastAttempt = NewWork(attempts: MaxAttempts);
        var retry = NewWork(attempts: 4);
        var batch = Batch(done1, done2, permanent, lastAttempt, retry);
        var result = new WorkBatchResult(batch.Items.Select(i => i.Id));
        result.Complete(done1.Id);
        result.Complete(done2.Id);
        result.Fail(permanent.Id, "too large", permanent: true);
        result.Fail(lastAttempt.Id, new InvalidOperationException("broker down"));
        result.Fail(retry.Id, "timeout");
        var work = new RecordingWorkRepository();
        var deadLettered = new List<Guid>();

        await OperationalLeasedWorker<BlobDeleteWork, BlobDeleteSettings>.SettleAsync(
            work, batch, result, MaxAttempts, stopping: false, handlerFailure: null,
            item => deadLettered.Add(item.Id), NullLogger.Instance, CancellationToken.None);

        Assert.HasCount(1, work.CompleteCalls, "completed rows are deleted in one statement");
        CollectionAssert.AreEquivalent(new[] { done1.Id, done2.Id }, work.CompleteCalls[0].ToList());
        CollectionAssert.AreEquivalent(new[] { permanent.Id, lastAttempt.Id }, work.DeadLettered.Keys.ToList());
        Assert.AreEqual("InvalidOperationException: broker down", work.DeadLettered[lastAttempt.Id]);
        Assert.HasCount(1, work.Released);
        Assert.AreEqual((retry.Id, 4, "timeout"), work.Released[0]);
        CollectionAssert.AreEquivalent(new[] { permanent.Id, lastAttempt.Id }, deadLettered);
        Assert.IsTrue(work.LeaseTokens.All(t => t == batch.LeaseToken),
            "Settlement must carry the batch lease token, or a stolen lease could be settled.");
    }

    /// <summary>
    /// Verifies the configured ceiling, not a constant, decides when a row parks: with MaxAttempts 3 a failure on
    /// attempt 3 dead-letters and one on attempt 2 is released.
    /// </summary>
    [TestMethod]
    public async Task Given_ConfiguredMaxAttempts_When_Settled_Then_TheCeilingIsHonored()
    {
        var third = NewWork(attempts: 3);
        var second = NewWork(attempts: 2);
        var batch = Batch(third, second);
        var result = new WorkBatchResult(batch.Items.Select(i => i.Id));
        result.Fail(third.Id, "fail");
        result.Fail(second.Id, "fail");
        var work = new RecordingWorkRepository();

        await OperationalLeasedWorker<BlobDeleteWork, BlobDeleteSettings>.SettleAsync(
            work, batch, result, maxAttempts: 3, stopping: false, handlerFailure: null, null,
            NullLogger.Instance, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { third.Id }, work.DeadLettered.Keys.ToList());
        CollectionAssert.AreEqual(new[] { second.Id }, work.Released.Select(r => r.Id).ToList());
    }

    /// <summary>
    /// Verifies unreported rows are handed back without consuming an attempt when the host is stopping, and are
    /// released with the handler's failure otherwise.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Given_UnreportedRows_When_Settled_Then_AbandonedOnlyWhenStopping(bool stopping)
    {
        var reported = NewWork(attempts: 1);
        var unreported = NewWork(attempts: 1);
        var batch = Batch(reported, unreported);
        var result = new WorkBatchResult(batch.Items.Select(i => i.Id));
        result.Complete(reported.Id);
        var work = new RecordingWorkRepository();

        await OperationalLeasedWorker<BlobDeleteWork, BlobDeleteSettings>.SettleAsync(
            work, batch, result, MaxAttempts, stopping, new TimeoutException("storage timed out"), null,
            NullLogger.Instance, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { reported.Id }, work.CompleteCalls.Single().ToList());
        if (stopping)
        {
            CollectionAssert.AreEqual(new[] { unreported.Id }, work.Abandoned);
            Assert.IsEmpty(work.Released);
        }
        else
        {
            Assert.IsEmpty(work.Abandoned);
            Assert.AreEqual((unreported.Id, 1, "TimeoutException: storage timed out"), work.Released.Single());
        }
    }

    /// <summary>A dead-letter that changed no row (lease lost) is not counted as dead-lettered.</summary>
    [TestMethod]
    public async Task Given_LostLease_When_DeadLettering_Then_ItIsNotReportedAsDeadLettered()
    {
        var item = NewWork(attempts: MaxAttempts);
        var batch = Batch(item);
        var result = new WorkBatchResult([item.Id]);
        result.Fail(item.Id, "fail");
        var work = new RecordingWorkRepository { LeaseLost = true };
        var deadLettered = 0;

        await OperationalLeasedWorker<BlobDeleteWork, BlobDeleteSettings>.SettleAsync(
            work, batch, result, MaxAttempts, stopping: false, handlerFailure: null, _ => deadLettered++,
            NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(0, deadLettered);
    }

    /// <summary>
    /// End to end through the worker: the host stops while the handler is mid-batch. The rows it reported are
    /// still settled - on a token the stop did not cancel - the rest are abandoned, and the cancellation still
    /// ends the loop. The claim itself is made with the configured attempt ceiling.
    /// </summary>
    [TestMethod]
    public async Task Given_StopDuringHandler_When_Processed_Then_ReportedRowsSettleOnAnUncancelledToken()
    {
        var done = NewWork(attempts: 1);
        var pending = NewWork(attempts: 1);
        var work = new RecordingWorkRepository { Claim = Batch(done, pending) };
        using var stopping = new CancellationTokenSource();
        var worker = new ProbeWorker(work, new BlobDeleteSettings { MaxAttempts = 7 }, async (items, result, ct) =>
        {
            result.Complete(done.Id);
            await stopping.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => worker.RunOnceAsync(stopping.Token));

        Assert.AreEqual(7, work.ClaimedMaxAttempts, "the claim must use the configured MaxAttempts");
        CollectionAssert.AreEqual(new[] { done.Id }, work.CompleteCalls.Single().ToList());
        CollectionAssert.AreEqual(new[] { pending.Id }, work.Abandoned);
        Assert.IsFalse(work.SettledOnCancelledToken,
            "settlement ran on the stopping token; a shutdown after a successful send would leave the rows leased");
    }

    /// <summary>A handler that throws does not strand its batch: every unreported row is released with backoff.</summary>
    [TestMethod]
    public async Task Given_HandlerThrows_When_Processed_Then_UnreportedRowsAreReleased()
    {
        var item = NewWork(attempts: 2);
        var work = new RecordingWorkRepository { Claim = Batch(item) };
        var worker = new ProbeWorker(work, new BlobDeleteSettings(),
            (_, _, _) => throw new InvalidOperationException("handler bug"));

        var processed = await worker.RunOnceAsync(CancellationToken.None);

        Assert.AreEqual(1, processed);
        Assert.AreEqual((item.Id, 2, "InvalidOperationException: handler bug"), work.Released.Single());
    }

    /// <summary>Verifies concurrent reports keep exactly one outcome per row, the first.</summary>
    [TestMethod]
    public async Task Given_ConcurrentReports_When_Recorded_Then_FirstReportWinsPerRow()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.CreateVersion7()).ToList();
        var result = new WorkBatchResult(ids);

        await Task.WhenAll(ids.SelectMany(id => new[]
        {
            Task.Run(() => result.Complete(id), TestContext.CancellationToken),
            Task.Run(() => result.Fail(id, "late"), TestContext.CancellationToken)
        }));

        Assert.IsTrue(ids.All(id => result.OutcomeOf(id) is not null));
        result.Complete(ids[0]);
        var first = result.OutcomeOf(ids[0]);
        result.Fail(ids[0], "later still");
        Assert.AreSame(first, result.OutcomeOf(ids[0]), "a report after the first must not replace it");
    }

    /// <summary>A report for a row the batch does not own is a handler bug, not a silent no-op.</summary>
    [TestMethod]
    public void Given_ForeignId_When_Reported_Then_Throws()
    {
        var result = new WorkBatchResult([Guid.CreateVersion7()]);

        Assert.ThrowsExactly<ArgumentException>(() => result.Complete(Guid.CreateVersion7()));
    }

    private static BlobDeleteWork NewWork(int attempts) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        ContainerName = "attachments",
        BlobName = $"blob-{Guid.NewGuid():N}",
        AttemptCount = attempts
    };

    private static LeasedBatch<BlobDeleteWork> Batch(params BlobDeleteWork[] items) => new(Guid.CreateVersion7(), items);

    /// <summary>Worker over a fixed repository; exposes one poll.</summary>
    private sealed class ProbeWorker(
        RecordingWorkRepository work,
        BlobDeleteSettings settings,
        Func<IReadOnlyList<BlobDeleteWork>, WorkBatchResult, CancellationToken, Task> handle)
        : OperationalLeasedWorker<BlobDeleteWork, BlobDeleteSettings>(
            new ServiceCollection().AddScoped<IOperationalWorkRepository>(_ => work)
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new FixedOptionsMonitor(settings),
            NullLoggerFactory.Instance)
    {
        public Task<int> RunOnceAsync(CancellationToken ct) => ProcessBatchAsync(ct);

        protected override Task HandleBatchAsync(
            IServiceProvider scope, IReadOnlyList<BlobDeleteWork> items, WorkBatchResult result, CancellationToken ct)
            => handle(items, result, ct);
    }

    private sealed class FixedOptionsMonitor(BlobDeleteSettings value) : IOptionsMonitor<BlobDeleteSettings>
    {
        public BlobDeleteSettings CurrentValue => value;

        public BlobDeleteSettings Get(string? name) => value;

        public IDisposable? OnChange(Action<BlobDeleteSettings, string?> listener) => null;
    }

    /// <summary>Records every settlement statement and the token it ran on.</summary>
    private sealed class RecordingWorkRepository : IOperationalWorkRepository
    {
        public LeasedBatch<BlobDeleteWork>? Claim { get; init; }
        public bool LeaseLost { get; init; }
        public int ClaimedMaxAttempts { get; private set; }
        public bool SettledOnCancelledToken { get; private set; }
        public List<IReadOnlyCollection<Guid>> CompleteCalls { get; } = [];
        public List<(Guid Id, int Attempts, string Error)> Released { get; } = [];
        public Dictionary<Guid, string> DeadLettered { get; } = [];
        public List<Guid> Abandoned { get; } = [];
        public List<Guid> LeaseTokens { get; } = [];

        public Task<LeasedBatch<TWork>> ClaimAsync<TWork>(
            int batchSize, TimeSpan leaseDuration, int maxAttempts, string owner, CancellationToken ct)
            where TWork : OperationalWorkBase
        {
            ClaimedMaxAttempts = maxAttempts;
            return Task.FromResult((LeasedBatch<TWork>)(object)(Claim ?? LeasedBatch<BlobDeleteWork>.Empty));
        }

        public Task<int> CompleteAsync<TWork>(Guid leaseToken, IReadOnlyCollection<Guid> ids, CancellationToken ct)
            where TWork : OperationalWorkBase
        {
            Record(leaseToken, ct);
            CompleteCalls.Add([.. ids]);
            return Task.FromResult(LeaseLost ? 0 : ids.Count);
        }

        public Task<bool> ReleaseAsync<TWork>(Guid leaseToken, Guid id, int attemptCount, string error, CancellationToken ct)
            where TWork : OperationalWorkBase
        {
            Record(leaseToken, ct);
            Released.Add((id, attemptCount, error));
            return Task.FromResult(!LeaseLost);
        }

        public Task<bool> DeadLetterAsync<TWork>(Guid leaseToken, Guid id, string error, CancellationToken ct)
            where TWork : OperationalWorkBase
        {
            Record(leaseToken, ct);
            DeadLettered[id] = error;
            return Task.FromResult(!LeaseLost);
        }

        public Task<int> AbandonAsync<TWork>(Guid leaseToken, IReadOnlyCollection<Guid> ids, CancellationToken ct)
            where TWork : OperationalWorkBase
        {
            Record(leaseToken, ct);
            Abandoned.AddRange(ids);
            return Task.FromResult(LeaseLost ? 0 : ids.Count);
        }

        public Task<bool> RetryDeadLetteredAsync<TWork>(Guid id, CancellationToken ct)
            where TWork : OperationalWorkBase => throw new NotSupportedException();

        public Task<int> PurgeDeadLetteredAsync<TWork>(DateTimeOffset cutoffUtc, CancellationToken ct)
            where TWork : OperationalWorkBase => throw new NotSupportedException();

        public Task<OutboxBacklog> GetOutboxBacklogAsync(CancellationToken ct) => throw new NotSupportedException();

        private void Record(Guid leaseToken, CancellationToken ct)
        {
            LeaseTokens.Add(leaseToken);
            SettledOnCancelledToken |= ct.IsCancellationRequested;
        }
    }
}
