extern alias SchedulerHost;

using System.Data.Common;
using System.Diagnostics.Metrics;
using EF.BackgroundServices.Scheduling;
using EF.Data.Contracts;
using EF.Data.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Repositories;
using SchedulerHost::TaskFlow.Scheduler.Handlers;
using Test.Integration.Infrastructure;
using Test.Support;

namespace Test.Integration;

/// <summary>
/// The scheduler's transactional steps under a retried commit, on the selected provider lane. A transaction
/// interceptor throws one exception the configured execution strategy treats as transient, either before the
/// commit (it does not land) or after it (it landed, the caller sees a failure). Either way the strategy re-runs
/// the step, and the job must still complete with one outbox row per message id. The reported count is the
/// committed attempt's: after a lost commit the retry redoes the work and reports it once; after a landed commit
/// the retry is a no-op and reports 0, because the client cannot tell a landed commit from a lost one (D-009).
/// Each run first drains candidates other classes left behind, because the jobs are cross-tenant and the
/// reported count is job-wide. The same class covers rows already written by someone else: a competing overdue
/// replica, and a recurrence pointer re-seeded over occurrences that exist; each run stages only what it wrote.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class SchedulerTransactionRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Applies migrations once for the class.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError)) return;
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Inconclusive without a container runtime; fails when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    /// <summary>Overdue: the retried step marks and announces each task once and reports them once.</summary>
    [TestMethod]
    [DataRow(CommitFault.BeforeCommit)]
    [DataRow(CommitFault.AfterCommit)]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task OverdueTaskCheck_TransientCommitFailure_RetriesToOneAnnouncementPerTask(CommitFault mode)
    {
        await RunOverdueAsync(null);
        var tenantId = Guid.NewGuid();
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var task = TaskItem.Create(TenantId.From(tenantId), $"Overdue {i}").Value!;
                task.UpdateDateRange(null, Now.AddDays(-2 - i));
                seed.TaskItems.Add(task);
            }
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        var fault = new TransientCommitFault(mode);
        var reported = await RunOverdueAsync(fault);

        Assert.AreEqual(1, fault.Faults, "the injected failure fired, so the step really was retried");
        Assert.AreEqual(Reported(mode, 3), reported, "the committed attempt's count, never one per attempt");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(3, await verify.Set<TaskItem>().IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == TenantId.From(tenantId) && t.OverdueNotifiedForDueDate != null,
                TestContext.CancellationToken));
        await AssertOneRowPerMessageIdAsync(verify.OutboxMessages
            .Where(m => m.Headers!.Contains(tenantId.ToString()) && m.EventType == nameof(TaskItemOverdueSuspectedEvent))
            .Select(m => m.Id), expected: 3);
    }

    /// <summary>Recurrence: the retried step writes each occurrence and its created event once.</summary>
    [TestMethod]
    [DataRow(CommitFault.BeforeCommit)]
    [DataRow(CommitFault.AfterCommit)]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task RecurringTaskGeneration_TransientCommitFailure_RetriesToOneEventPerOccurrence(CommitFault mode)
    {
        await RunRecurrenceAsync(null);
        var tenantId = Guid.NewGuid();
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            var template = TaskItem.Create(TenantId.From(tenantId), "Weekly report").Value!;
            template.Update(features: TaskFeatures.Recurring);
            template.UpdateDateRange(null, Now.AddDays(-3));
            template.UpdateRecurrencePattern(new RecurrencePattern { Frequency = RecurrencePattern.Daily, Interval = 1 });
            seed.TaskItems.Add(template);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        var fault = new TransientCommitFault(mode);
        var reported = await RunRecurrenceAsync(fault);

        Assert.AreEqual(1, fault.Faults, "the injected failure fired, so the step really was retried");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        var occurrenceIds = await verify.Set<TaskItem>().IgnoreQueryFilters()
            .Where(t => t.TenantId == TenantId.From(tenantId) && t.RecurrenceTemplateId != null)
            .Select(t => t.Id.Value)
            .ToListAsync(TestContext.CancellationToken);
        Assert.IsNotEmpty(occurrenceIds, "the due occurrences were materialized");
        Assert.AreEqual(Reported(mode, occurrenceIds.Count), reported, "the committed attempt's count, never one per attempt");
        await AssertOneRowPerMessageIdAsync(verify.OutboxMessages
            .Where(m => m.Headers!.Contains(tenantId.ToString()) && occurrenceIds.Contains(m.Id))
            .Select(m => m.Id), expected: occurrenceIds.Count);
    }

    /// <summary>Stale cleanup: the retried step queues the blob deletion once .</summary>
    [TestMethod]
    [DataRow(CommitFault.BeforeCommit)]
    [DataRow(CommitFault.AfterCommit)]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task StaleTaskCleanup_TransientCommitFailure_RetriesToOneBlobWorkRow(CommitFault mode)
    {
        await RunStaleCleanupAsync(null);
        var tenantId = Guid.NewGuid();
        var typedTenantId = TenantId.From(tenantId);
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            var task = TaskItem.Create(typedTenantId, "Cancelled long ago").Value!;
            task.TransitionStatus(TaskItemStatus.Cancelled);
            seed.TaskItems.Add(task);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            await seed.Set<TaskItem>().IgnoreQueryFilters()
                .Where(t => t.Id == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.TerminalAtUtc, Now.AddDays(-200)), TestContext.CancellationToken);
            seed.Attachments.Add(Attachment.Create(
                typedTenantId, "spec.pdf", "application/pdf", 1024, "https://example/spec.pdf",
                AttachmentOwnerType.TaskItem, task.Id.Value, storageKey: $"{tenantId}/{task.Id.Value}/spec").Value!);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        var fault = new TransientCommitFault(mode);
        var reported = await RunStaleCleanupAsync(fault);

        Assert.AreEqual(1, fault.Faults, "the injected failure fired, so the step really was retried");
        Assert.AreEqual(Reported(mode, 1), reported, "the committed attempt's count, never one per attempt");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(0, await verify.Set<TaskItem>().IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == typedTenantId, TestContext.CancellationToken));
        await AssertOneRowPerMessageIdAsync(verify.BlobDeleteWork
            .Where(w => w.TenantId == tenantId)
            .Select(w => w.Id), expected: 1);
    }

    /// <summary>
    /// Overdue, two replicas: between this run's scan and its mark another replica marks and announces two of the
    /// three tasks. This run must announce only the task it marked itself, not re-stage the other replica's ids.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task OverdueTaskCheck_CompetingReplicaMarkedSome_StagesOnlyTheRestOnce()
    {
        await RunOverdueAsync(null);
        var tenantId = Guid.NewGuid();
        var rows = new List<OverdueTaskRow>();
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var task = TaskItem.Create(TenantId.From(tenantId), $"Raced {i}").Value!;
                task.UpdateDateRange(null, Now.AddDays(-2 - i));
                seed.TaskItems.Add(task);
                rows.Add(new OverdueTaskRow(tenantId, task.Id.Value, task.DueDate!.Value));
            }
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        var replica = new CompetingOverdueReplica(rows.Take(2).ToList());
        var reported = await RunOverdueAsync(replica);

        Assert.IsTrue(replica.Ran, "the other replica marked its rows inside this run's window");
        Assert.AreEqual(1, reported, "this run reports only the task it marked");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(3, await verify.Set<TaskItem>().IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == TenantId.From(tenantId) && t.OverdueNotifiedForDueDate != null,
                TestContext.CancellationToken));
        await AssertOneRowPerMessageIdAsync(verify.OutboxMessages
            .Where(m => m.Headers!.Contains(tenantId.ToString()) && m.EventType == nameof(TaskItemOverdueSuspectedEvent))
            .Select(m => m.Id), expected: 3);
    }

    /// <summary>
    /// Recurrence, re-seeded pointer: a template's next-occurrence pointer moves back over occurrences it already
    /// generated (the pattern was removed and attached again, which re-seeds it from the due date). The upsert keeps
    /// the stored occurrences; the run must announce only the occurrences it inserted.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task RecurringTaskGeneration_PointerReseededOverGeneratedOccurrences_StagesOnlyNewOnes()
    {
        // Drained at the later clock, so nothing another class left behind is due at either clock below.
        await RunRecurrenceAsync(null);
        var tenantId = Guid.NewGuid();
        var dueFrom = Now.AddDays(-3);
        TaskItemId templateId;
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            var template = TaskItem.Create(TenantId.From(tenantId), "Daily standup").Value!;
            template.Update(features: TaskFeatures.Recurring);
            template.UpdateDateRange(null, dueFrom);
            template.UpdateRecurrencePattern(new RecurrencePattern { Frequency = RecurrencePattern.Daily, Interval = 1 });
            seed.TaskItems.Add(template);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            templateId = template.Id;
        }

        await RunRecurrenceAsync(null, Now.AddDays(-1));
        var firstCount = (await OccurrenceIdsAsync(tenantId)).Count;
        Assert.IsGreaterThan(0, firstCount, "the first run generated occurrences");
        await using (var reseed = DbContainerFixture.CreateTrxnContext())
        {
            await reseed.Set<TaskItem>().IgnoreQueryFilters()
                .Where(t => t.Id == templateId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.NextOccurrenceAtUtc, dueFrom), TestContext.CancellationToken);
        }

        var reported = await RunRecurrenceAsync(null);

        var occurrenceIds = await OccurrenceIdsAsync(tenantId);
        Assert.IsGreaterThan(firstCount, occurrenceIds.Count, "the later clock adds occurrences past the first run");
        Assert.AreEqual(occurrenceIds.Count - firstCount, reported, "only the occurrences this run inserted are reported");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        await AssertOneRowPerMessageIdAsync(verify.OutboxMessages
            .Where(m => m.Headers!.Contains(tenantId.ToString()) && occurrenceIds.Contains(m.Id))
            .Select(m => m.Id), expected: occurrenceIds.Count);
    }

    /// <summary>
    /// Stale cleanup, two overlapping runs: just before this run's first write in the tenant step, another run
    /// removes two of the three stale tasks and queues their blob deletions. This run must queue blob work only for
    /// the task it removed itself.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task StaleTaskCleanup_CompetingRunRemovedSome_StagesBlobWorkOnlyForItsOwn()
    {
        await RunStaleCleanupAsync(null);
        var tenantId = Guid.NewGuid();
        var typedTenantId = TenantId.From(tenantId);
        var taskIds = new List<Guid>();
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var task = TaskItem.Create(typedTenantId, $"Cancelled {i}").Value!;
                task.TransitionStatus(TaskItemStatus.Cancelled);
                seed.TaskItems.Add(task);
                seed.Attachments.Add(Attachment.Create(
                    typedTenantId, $"spec-{i}.pdf", "application/pdf", 1024, $"https://example/spec-{i}.pdf",
                    AttachmentOwnerType.TaskItem, task.Id.Value, storageKey: $"{tenantId}/{task.Id.Value}/spec-{i}").Value!);
                taskIds.Add(task.Id.Value);
            }
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            var typedIds = taskIds.ConvertAll(TaskItemId.From);
            await seed.Set<TaskItem>().IgnoreQueryFilters()
                .Where(t => typedIds.Contains(t.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.TerminalAtUtc, Now.AddDays(-200)), TestContext.CancellationToken);
        }

        var competitor = new CompetingStaleRun(tenantId, taskIds.Take(2).ToList());
        var reported = await RunStaleCleanupAsync(competitor);

        Assert.IsTrue(competitor.Ran, "the other run removed its tasks inside this run's window");
        Assert.AreEqual(1, reported, "this run reports only the task it removed");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(0, await verify.Set<TaskItem>().IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == typedTenantId, TestContext.CancellationToken));
        Assert.AreEqual(0, await verify.Set<Attachment>().IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == typedTenantId, TestContext.CancellationToken));
        await AssertOneRowPerMessageIdAsync(verify.BlobDeleteWork
            .Where(w => w.TenantId == tenantId)
            .Select(w => w.Id), expected: 3);
    }

    private async Task<List<Guid>> OccurrenceIdsAsync(Guid tenantId)
    {
        await using var verify = DbContainerFixture.CreateTrxnContext();
        return await verify.Set<TaskItem>().IgnoreQueryFilters()
            .Where(t => t.TenantId == TenantId.From(tenantId) && t.RecurrenceTemplateId != null)
            .Select(t => t.Id.Value)
            .ToListAsync(TestContext.CancellationToken);
    }

    /// <summary>What the job reports: the rows the retry changed, all of them after a lost commit, none after a landed one.</summary>
    private static long Reported(CommitFault mode, int changed) => mode == CommitFault.BeforeCommit ? changed : 0;

    /// <summary>Exactly <paramref name="expected"/> rows, and no id among them twice.</summary>
    private async Task AssertOneRowPerMessageIdAsync(IQueryable<Guid> ids, int expected)
    {
        var rows = await ids.ToListAsync(TestContext.CancellationToken);
        Assert.HasCount(expected, rows, "one row per message id");
        Assert.HasCount(expected, rows.Distinct().ToList(), "no message id is stored twice");
    }

    private async Task<long> RunOverdueAsync(IInterceptor? interceptor)
    {
        using var telemetry = new RecordingTelemetry(OverdueTaskCheckHandler.JobName);
        await using var db = CreateContext(interceptor);
        await new OverdueTaskCheckHandler(
            new TaskItemSystemRepository(db),
            new OutboxStaging<TaskFlowDbContextTrxn>(db, TestOutbox.Options),
            telemetry.Telemetry,
            TimeProvider.System,
            NullLogger<OverdueTaskCheckHandler>.Instance).HandleAsync(TestContext.CancellationToken);
        return telemetry.RowsAffected;
    }

    private async Task<long> RunRecurrenceAsync(IInterceptor? interceptor, DateTimeOffset? asOfUtc = null)
    {
        using var telemetry = new RecordingTelemetry(RecurringTaskGenerationHandler.JobName);
        await using var db = CreateContext(interceptor);
        await new RecurringTaskGenerationHandler(
            new TaskItemSystemRepository(db),
            new OutboxStaging<TaskFlowDbContextTrxn>(db, TestOutbox.Options),
            telemetry.Telemetry,
            new FixedTimeProvider(asOfUtc ?? Now),
            NullLogger<RecurringTaskGenerationHandler>.Instance).HandleAsync(TestContext.CancellationToken);
        return telemetry.RowsAffected;
    }

    private async Task<long> RunStaleCleanupAsync(IInterceptor? interceptor)
    {
        using var telemetry = new RecordingTelemetry(StaleTaskCleanupHandler.JobName);
        await using var db = CreateContext(interceptor);
        await new StaleTaskCleanupHandler(
            new TaskItemSystemRepository(db),
            telemetry.Telemetry,
            TimeProvider.System,
            new ConfigurationBuilder().Build(),
            NullLogger<StaleTaskCleanupHandler>.Instance).HandleAsync(TestContext.CancellationToken);
        return telemetry.RowsAffected;
    }

    private static TaskFlowDbContextTrxn CreateContext(IInterceptor? interceptor) =>
        interceptor is null ? DbContainerFixture.CreateTrxnContext() : DbContainerFixture.CreateTrxnContext(null, interceptor);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Where the injected failure lands relative to the database commit.</summary>
    public enum CommitFault
    {
        /// <summary>Thrown from TransactionCommitting: the transaction rolls back.</summary>
        BeforeCommit,

        /// <summary>Thrown from TransactionCommitted: the rows are stored, the caller sees a failure.</summary>
        AfterCommit
    }

    /// <summary>
    /// Throws once, on the first commit, an exception the lane's configured retrying strategy retries: a
    /// PostgresException with SqlState 40001 (serialization failure, <c>IsTransient</c>) for Npgsql, and a
    /// <see cref="TimeoutException"/> for SQL Server, which <c>SqlServerTransientExceptionDetector</c> retries
    /// and which, unlike a SqlException, can be constructed without reflection.
    /// </summary>
    private sealed class TransientCommitFault(CommitFault mode) : DbTransactionInterceptor
    {
        public int Faults { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (mode == CommitFault.BeforeCommit) ThrowOnce();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (mode == CommitFault.AfterCommit) ThrowOnce();
            return Task.CompletedTask;
        }

        private void ThrowOnce()
        {
            if (Faults > 0) return;
            Faults++;
            throw DbContainerFixture.Provider == TaskFlowDbProvider.PostgreSql
                ? new PostgresException("injected serialization failure", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure)
                : new TimeoutException("injected commit timeout");
        }
    }

    /// <summary>
    /// A second cleanup run that, just before this run's first write inside its tenant step, queues the blob
    /// deletions for <paramref name="taskIds"/> through the same repository call this job uses, then deletes their
    /// attachments and the tasks, each statement committing on its own. Runs once. The hook is the first write, not
    /// the delete: a competitor blocked behind rows this run already wrote would wait on this run forever.
    /// </summary>
    private sealed class CompetingStaleRun(Guid tenantId, List<Guid> taskIds) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunBeforeFirstWriteAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RunBeforeFirstWriteAsync(command, cancellationToken);
            return result;
        }

        private async Task RunBeforeFirstWriteAsync(DbCommand command, CancellationToken ct)
        {
            if (Ran || command.Transaction is null
                || command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return;
            Ran = true;

            await using var other = DbContainerFixture.CreateTrxnContext();
            await new TaskItemSystemRepository(other).StageBlobDeletesAsync(tenantId, taskIds, ct);
            var typedTenantId = TenantId.From(tenantId);
            var typedIds = taskIds.ConvertAll(TaskItemId.From);
            await other.Set<Attachment>().IgnoreQueryFilters()
                .Where(a => a.TenantId == typedTenantId && a.OwnerType == AttachmentOwnerType.TaskItem && taskIds.Contains(a.OwnerId))
                .ExecuteDeleteAsync(ct);
            await other.Set<TaskItem>().IgnoreQueryFilters()
                .Where(t => t.TenantId == typedTenantId && typedIds.Contains(t.Id))
                .ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// A second replica that, just before this run's first UPDATE (its overdue mark), marks <paramref name="rows"/> on
    /// another connection and stages the same announcements this job would, then commits. Runs once.
    /// </summary>
    private sealed class CompetingOverdueReplica(IReadOnlyList<OverdueTaskRow> rows) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Ran || !command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)) return result;
            Ran = true;

            await using var other = DbContainerFixture.CreateTrxnContext();
            var ids = rows.Select(r => TaskItemId.From(r.Id)).ToList();
            await other.Set<TaskItem>().IgnoreQueryFilters()
                .Where(t => ids.Contains(t.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.OverdueNotifiedForDueDate, t => t.DueDate), cancellationToken);
            var outbox = new OutboxStaging<TaskFlowDbContextTrxn>(other, TestOutbox.Options);
            foreach (var row in rows) outbox.Stage(OverdueTaskCheckHandler.Announcement(row, DateTimeOffset.UtcNow));
            await other.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: cancellationToken);
            return result;
        }
    }

    /// <summary>Job telemetry over a private meter factory, summing the rows-affected the job reports.</summary>
    private sealed class RecordingTelemetry : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        private readonly MeterListener _listener = new();
        private long _rowsAffected;

        public RecordingTelemetry(string jobName)
        {
            var factory = _services.GetRequiredService<IMeterFactory>();
            Telemetry = new ScheduledJobTelemetry(factory, Options.Create(new ScheduledJobTelemetryOptions()));
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "scheduler.job.rows_affected")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "job.name" && Equals(tag.Value, jobName)) Interlocked.Add(ref _rowsAffected, value);
                }
            });
            _listener.Start();
        }

        public ScheduledJobTelemetry Telemetry { get; }

        public long RowsAffected => Interlocked.Read(ref _rowsAffected);

        public void Dispose()
        {
            _listener.Dispose();
            _services.Dispose();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
