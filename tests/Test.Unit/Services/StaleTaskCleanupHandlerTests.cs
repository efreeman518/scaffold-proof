using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Scheduler.Handlers;
using Test.Support;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="StaleTaskCleanupHandler"/>: blob deletions are recorded before the rows they point at
/// disappear, the retention cutoff is re-asserted on the delete, and paging resumes past the last row scanned
/// so a task skipped for still having subtasks is not re-read for the rest of the run.
/// Pure-unit tier: the system repository is an in-memory fake.
/// </summary>
[TestClass]
public class StaleTaskCleanupHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = TestConstants.TenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000099");

    private readonly FakeTaskItemSystemRepository _repo = new();

    /// <summary>The guarded delete runs first; blob work and the attachment delete follow in the same transaction.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_DeletesTaskThenQueuesBlobWork_InsideOneTransaction()
    {
        _repo.StaleBatches.Enqueue([new StaleTaskRow(TenantA, Guid.CreateVersion7())]);

        await Handler().HandleAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(FakeTaskItemSystemRepository.GetStaleBatchAsync),
                "BeginTransaction",
                nameof(FakeTaskItemSystemRepository.DeleteStaleTaskAsync),
                nameof(FakeTaskItemSystemRepository.StageBlobDeletesAsync),
                nameof(FakeTaskItemSystemRepository.DeleteAttachmentsAsync),
                "Commit"
            },
            _repo.Calls);
    }

    /// <summary>Each tenant in a page is deleted in its own transaction: EF cannot express a tuple IN.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_GroupsPageByTenant()
    {
        _repo.StaleBatches.Enqueue(
        [
            new StaleTaskRow(TenantA, Guid.CreateVersion7()),
            new StaleTaskRow(TenantB, Guid.CreateVersion7()),
            new StaleTaskRow(TenantA, Guid.CreateVersion7())
        ]);

        await Handler().HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(2, _repo.TransactionCount);
        Assert.AreEqual(3, _repo.Deletes.Count);
        Assert.AreEqual(2, _repo.StagedBlobDeletes[0].Ids.Count);
        Assert.AreEqual(1, _repo.StagedBlobDeletes[1].Ids.Count);
    }

    /// <summary>
    /// Blob work is queued only for tasks this run's guarded delete removed: a task another run removed first
    /// already has its work rows under the same deterministic ids. A step that removed nothing queues nothing.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_TaskNotRemovedByThisRun_GetsNoBlobWork()
    {
        var lost = Guid.CreateVersion7();
        var won = Guid.CreateVersion7();
        var lostAlone = Guid.CreateVersion7();
        _repo.StaleBatches.Enqueue(
        [
            new StaleTaskRow(TenantA, lost),
            new StaleTaskRow(TenantA, won),
            new StaleTaskRow(TenantB, lostAlone)
        ]);
        _repo.NotDeleted.UnionWith([lost, lostAlone]);

        await Handler().HandleAsync(TestContext.CancellationToken);

        Assert.HasCount(1, _repo.StagedBlobDeletes);
        CollectionAssert.AreEqual(new[] { won }, _repo.StagedBlobDeletes[0].Ids.ToArray());
    }

    /// <summary>The configured retention window is what reaches the delete predicate.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_PassesConfiguredRetentionCutoff()
    {
        _repo.StaleBatches.Enqueue([new StaleTaskRow(TenantA, Guid.CreateVersion7())]);

        await Handler(retentionDays: 30).HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(Now.AddDays(-30), _repo.Deletes[0].Cutoff);
    }

    /// <summary>An empty batch ends the run without a transaction.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_NoStaleTasks_DoesNothing()
    {
        await Handler().HandleAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, _repo.TransactionCount);
        Assert.AreEqual(0, _repo.Deletes.Count);
    }

    /// <summary>Builds the handler over an in-memory configuration.</summary>
    private StaleTaskCleanupHandler Handler(int? retentionDays = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(retentionDays is null
                ? []
                : new Dictionary<string, string?>
                {
                    ["Scheduling:StaleCleanup:RetentionDays"] = retentionDays.Value.ToString()
                })
            .Build();

        return new StaleTaskCleanupHandler(
            _repo,
            SchedulerTestTelemetry.Create(),
            new FixedTimeProvider(Now),
            config,
            NullLogger<StaleTaskCleanupHandler>.Instance);
    }

    public TestContext TestContext { get; set; } = null!;
}
