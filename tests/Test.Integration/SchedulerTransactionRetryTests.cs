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
/// the step, and the job must still complete with one outbox row per message id and a reported count equal to
/// the rows it actually changed. Each run first drains candidates other classes left behind, because the jobs are
/// cross-tenant and the reported count is job-wide.
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

    /// <summary>Overdue: the retried step marks and announces each task once and reports three.</summary>
    [TestMethod]
    [DataRow(CommitFault.BeforeCommit)]
    [DataRow(CommitFault.AfterCommit)]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task OverdueTaskCheck_TransientCommitFailure_RetriesToOneAnnouncementPerTask(CommitFault mode)
    {
        await RunOverdueAsync(fault: null);
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
        Assert.AreEqual(3, reported, "the job reports each task once, not once per attempt");
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
        await RunRecurrenceAsync(fault: null);
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
        Assert.AreEqual(occurrenceIds.Count, reported, "the job reports each occurrence once, not once per attempt");
        await AssertOneRowPerMessageIdAsync(verify.OutboxMessages
            .Where(m => m.Headers!.Contains(tenantId.ToString()) && occurrenceIds.Contains(m.Id))
            .Select(m => m.Id), expected: occurrenceIds.Count);
    }

    /// <summary>Stale cleanup: the retried step queues the blob deletion once and reports one deleted task.</summary>
    [TestMethod]
    [DataRow(CommitFault.BeforeCommit)]
    [DataRow(CommitFault.AfterCommit)]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task StaleTaskCleanup_TransientCommitFailure_RetriesToOneBlobWorkRow(CommitFault mode)
    {
        await RunStaleCleanupAsync(fault: null);
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
                AttachmentOwnerType.TaskItem, task.Id.Value).Value!);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        var fault = new TransientCommitFault(mode);
        var reported = await RunStaleCleanupAsync(fault);

        Assert.AreEqual(1, fault.Faults, "the injected failure fired, so the step really was retried");
        Assert.AreEqual(1, reported, "the job reports the deleted task once, not once per attempt");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(0, await verify.Set<TaskItem>().IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == typedTenantId, TestContext.CancellationToken));
        await AssertOneRowPerMessageIdAsync(verify.BlobDeleteWork
            .Where(w => w.TenantId == tenantId)
            .Select(w => w.Id), expected: 1);
    }

    /// <summary>Exactly <paramref name="expected"/> rows, and no id among them twice.</summary>
    private async Task AssertOneRowPerMessageIdAsync(IQueryable<Guid> ids, int expected)
    {
        var rows = await ids.ToListAsync(TestContext.CancellationToken);
        Assert.HasCount(expected, rows, "one row per message id");
        Assert.HasCount(expected, rows.Distinct().ToList(), "no message id is stored twice");
    }

    private async Task<long> RunOverdueAsync(TransientCommitFault? fault)
    {
        using var telemetry = new RecordingTelemetry(OverdueTaskCheckHandler.JobName);
        await using var db = CreateContext(fault);
        await new OverdueTaskCheckHandler(
            new TaskItemSystemRepository(db),
            new OutboxStaging<TaskFlowDbContextTrxn>(db, TestOutbox.Options),
            telemetry.Telemetry,
            TimeProvider.System,
            NullLogger<OverdueTaskCheckHandler>.Instance).HandleAsync(TestContext.CancellationToken);
        return telemetry.RowsAffected;
    }

    private async Task<long> RunRecurrenceAsync(TransientCommitFault? fault)
    {
        using var telemetry = new RecordingTelemetry(RecurringTaskGenerationHandler.JobName);
        await using var db = CreateContext(fault);
        await new RecurringTaskGenerationHandler(
            new TaskItemSystemRepository(db),
            new OutboxStaging<TaskFlowDbContextTrxn>(db, TestOutbox.Options),
            telemetry.Telemetry,
            new FixedTimeProvider(Now),
            NullLogger<RecurringTaskGenerationHandler>.Instance).HandleAsync(TestContext.CancellationToken);
        return telemetry.RowsAffected;
    }

    private async Task<long> RunStaleCleanupAsync(TransientCommitFault? fault)
    {
        using var telemetry = new RecordingTelemetry(StaleTaskCleanupHandler.JobName);
        await using var db = CreateContext(fault);
        await new StaleTaskCleanupHandler(
            new TaskItemSystemRepository(db),
            telemetry.Telemetry,
            TimeProvider.System,
            new ConfigurationBuilder().Build(),
            NullLogger<StaleTaskCleanupHandler>.Instance).HandleAsync(TestContext.CancellationToken);
        return telemetry.RowsAffected;
    }

    private static TaskFlowDbContextTrxn CreateContext(TransientCommitFault? fault) =>
        fault is null ? DbContainerFixture.CreateTrxnContext() : DbContainerFixture.CreateTrxnContext(null, fault);

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
