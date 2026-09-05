extern alias SchedulerHost;

using EF.Data.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;
using TaskFlow.Observability.Meters;
using SchedulerHost::TaskFlow.Scheduler.Handlers;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Validates the three scheduler jobs against a real database on the selected provider lane. The property
/// that matters for every one of them is the same: running twice must not do the work twice. A second run
/// finds no candidates and stages no additional outbox rows, which is what makes a crashed or replayed job
/// safe to simply run again.
/// Component tier: contexts are built directly against the standalone database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class SchedulerJobIntegrationTests
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

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    /// <summary>Overdue: first run marks and announces, second run finds nothing and stages nothing.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task OverdueTaskCheck_RunTwice_SecondRunIsANoOp()
    {
        var tenantId = Guid.NewGuid();
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var task = TaskItem.Create(DomainId.From<TenantId>(tenantId), $"Overdue {i}").Value!;
                task.UpdateDateRange(null, Now.AddDays(-2 - i));
                seed.TaskItems.Add(task);
            }
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        var first = await RunOverdueAsync(tenantId);
        Assert.AreEqual(3, first.Notified, "first run marks every candidate");
        Assert.AreEqual(3, first.OutboxRows, "one announcement per candidate");

        var second = await RunOverdueAsync(tenantId);
        Assert.AreEqual(3, second.Notified, "nothing new to mark");
        Assert.AreEqual(3, second.OutboxRows, "and no additional outbox rows are staged");
    }

    /// <summary>Recurrence: the second run generates no further occurrences and no further events.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task RecurringTaskGeneration_RunTwice_SecondRunIsANoOp()
    {
        var tenantId = Guid.NewGuid();
        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            var template = TaskItem.Create(DomainId.From<TenantId>(tenantId), "Weekly report").Value!;
            template.Update(features: TaskFeatures.Recurring);
            template.UpdateDateRange(null, Now.AddDays(-3));
            template.UpdateRecurrencePattern(new RecurrencePattern
            {
                Frequency = RecurrencePattern.Daily,
                Interval = 1
            });
            seed.TaskItems.Add(template);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        var first = await RunRecurrenceAsync(tenantId);
        Assert.IsGreaterThan(0, first.Occurrences, "first run materializes the due occurrences");
        Assert.AreEqual(first.Occurrences, first.OutboxRows, "one created event per occurrence");

        var second = await RunRecurrenceAsync(tenantId);
        Assert.AreEqual(first.Occurrences, second.Occurrences, "the template is no longer due");
        Assert.AreEqual(first.OutboxRows, second.OutboxRows, "and no additional outbox rows are staged");
    }

    /// <summary>
    /// Stale cleanup: the blob-delete work rows exist before the tasks and their attachments are gone, so a
    /// crash between the two leaves work to do rather than an orphaned blob nobody remembers.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task StaleTaskCleanup_StagesBlobWork_ThenDeletesAndIsANoOpOnRerun()
    {
        var tenantId = Guid.NewGuid();
        var typedTenantId = DomainId.From<TenantId>(tenantId);
        Guid taskId;

        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            var task = TaskItem.Create(typedTenantId, "Cancelled long ago").Value!;
            task.TransitionStatus(TaskItemStatus.Cancelled);
            seed.TaskItems.Add(task);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            taskId = task.Id.Value;

            // TerminalAtUtc is stamped by the domain at "now"; push it past the retention window directly,
            // because the point of the test is the cleanup, not the passage of time.
            await seed.Set<TaskItem>().IgnoreQueryFilters()
                .Where(t => t.Id == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.TerminalAtUtc, Now.AddDays(-200)),
                    TestContext.CancellationToken);

            var attachment = Attachment.Create(
                typedTenantId, "spec.pdf", "application/pdf", 1024, "https://example/spec.pdf",
                AttachmentOwnerType.TaskItem, taskId).Value!;
            seed.Attachments.Add(attachment);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await RunStaleCleanupAsync();

        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(1, await verify.BlobDeleteWork.CountAsync(w => w.TenantId == tenantId, TestContext.CancellationToken),
            "the deferred blob deletion survives the task it pointed at");
        Assert.AreEqual(0, await verify.Set<TaskItem>().IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == typedTenantId, TestContext.CancellationToken));
        Assert.AreEqual(0, await verify.Set<Attachment>().IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == typedTenantId, TestContext.CancellationToken));

        await RunStaleCleanupAsync();

        Assert.AreEqual(1, await verify.BlobDeleteWork.CountAsync(w => w.TenantId == tenantId, TestContext.CancellationToken),
            "a second run has nothing to stage");
    }

    /// <summary>Runs the overdue job and reports the resulting notified-task and outbox-row counts.</summary>
    private async Task<(int Notified, int OutboxRows)> RunOverdueAsync(Guid tenantId)
    {
        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            var handler = new OverdueTaskCheckHandler(
                new TaskItemSystemRepository(db),
                new OutboxStaging(db),
                new SchedulerJobMeter(),
                TimeProvider.System,
                NullLogger<OverdueTaskCheckHandler>.Instance);
            await handler.HandleAsync(TestContext.CancellationToken);
        }

        await using var verify = DbContainerFixture.CreateTrxnContext();
        var notified = await verify.Set<TaskItem>().IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == DomainId.From<TenantId>(tenantId)
                && t.OverdueNotifiedForDueDate != null, TestContext.CancellationToken);
        // Only the announcements this job stages: seeding a task raises its own created event through the
        // staging interceptor, and counting those would measure the fixture rather than the job.
        var outboxRows = await verify.OutboxMessages.CountAsync(
            m => m.TenantId == tenantId && m.EventType == nameof(TaskItemOverdueSuspectedEvent),
            TestContext.CancellationToken);

        return (notified, outboxRows);
    }

    /// <summary>Runs the recurrence job and reports occurrence and outbox counts.</summary>
    private async Task<(int Occurrences, int OutboxRows)> RunRecurrenceAsync(Guid tenantId)
    {
        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            var handler = new RecurringTaskGenerationHandler(
                new TaskItemSystemRepository(db),
                new OutboxStaging(db),
                new SchedulerJobMeter(),
                TimeProvider.System,
                NullLogger<RecurringTaskGenerationHandler>.Instance);
            await handler.HandleAsync(TestContext.CancellationToken);
        }

        await using var verify = DbContainerFixture.CreateTrxnContext();
        var occurrences = await verify.Set<TaskItem>().IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == DomainId.From<TenantId>(tenantId) && t.RecurrenceTemplateId != null,
                TestContext.CancellationToken);
        // The generated occurrences are upserted, which bypasses the staging interceptor, so every created
        // event whose message id is an occurrence id was staged by this job rather than by the seed.
        var occurrenceIds = await verify.Set<TaskItem>().IgnoreQueryFilters()
            .Where(t => t.TenantId == DomainId.From<TenantId>(tenantId) && t.RecurrenceTemplateId != null)
            .Select(t => t.Id.Value)
            .ToListAsync(TestContext.CancellationToken);
        var outboxRows = await verify.OutboxMessages.CountAsync(
            m => m.TenantId == tenantId && occurrenceIds.Contains(m.Id), TestContext.CancellationToken);

        return (occurrences, outboxRows);
    }

    /// <summary>Runs the stale-cleanup job with a 90-day retention window.</summary>
    private async Task RunStaleCleanupAsync()
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        var handler = new StaleTaskCleanupHandler(
            new TaskItemSystemRepository(db),
            new SchedulerJobMeter(),
            TimeProvider.System,
            new ConfigurationBuilder().Build(),
            NullLogger<StaleTaskCleanupHandler>.Instance);
        await handler.HandleAsync(TestContext.CancellationToken);
    }

    public TestContext TestContext { get; set; } = null!;
}
