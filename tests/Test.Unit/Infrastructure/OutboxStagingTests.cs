using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Interceptors;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-026: the staging interceptor turns raised domain events into outbox rows inside the same SaveChanges as
/// the domain write. If that ever became a second SaveChanges, an event could be published for a write that
/// rolled back - these tests are what fails when it does.
/// </summary>
[TestClass]
public sealed class OutboxStagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Save_StagesRaisedEvents_InTheSameSaveChanges()
    {
        var ct = TestContext.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var task = TaskItem.Create(DomainId.From<TenantId>(TestConstants.TenantId), "staged").Value!;

        await using var db = Create(dbName);
        db.TaskItems.Add(task);

        // The aggregate is holding the event; nothing is persisted yet.
        Assert.AreEqual(1, task.DomainEvents.Count);

        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);

        var rows = await db.OutboxMessages.ToListAsync(ct);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(nameof(TaskItemCreatedEvent), rows[0].EventType);
        Assert.AreEqual(1, rows[0].EventVersion);
        Assert.AreEqual(TestConstants.TenantId, rows[0].TenantId);
        Assert.AreEqual(OutboxStagingInterceptor.DefaultDestination, rows[0].Destination);
        Assert.AreEqual(Now, rows[0].OccurredAtUtc);
        Assert.AreEqual(0, rows[0].AttemptCount);
        Assert.IsNull(rows[0].LeaseToken);

        // Drained: a second save must not stage the same event again. The touch is a priority edit on
        // purpose - a title or description edit would legitimately raise TaskItemContentChangedEvent
        // (D-040) and this assertion is about re-staging, not about how many events an update raises.
        Assert.AreEqual(0, task.DomainEvents.Count);
        task.Update(priority: TaskFlow.Domain.Shared.Enums.Priority.High);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
        Assert.AreEqual(1, await db.OutboxMessages.CountAsync(ct));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Save_StagesOneRowPerRaisedEvent_OnCompletion()
    {
        var ct = TestContext.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var task = TaskItem.Create(DomainId.From<TenantId>(TestConstants.TenantId), "completing").Value!;

        await using var db = Create(dbName);
        db.TaskItems.Add(task);
        Assert.IsTrue(task.TransitionStatus(TaskItemStatus.InProgress).IsSuccess);
        Assert.IsTrue(task.TransitionStatus(TaskItemStatus.Completed).IsSuccess);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);

        var types = await db.OutboxMessages.Select(m => m.EventType).ToListAsync(ct);
        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(TaskItemCreatedEvent),
                nameof(TaskItemStatusChangedEvent),
                nameof(TaskItemStatusChangedEvent),
                nameof(TaskItemCompletedEvent)
            },
            types);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RaisingAnEvent_WritesNothing_UntilSaveChanges()
    {
        var ct = TestContext.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var task = TaskItem.Create(DomainId.From<TenantId>(TestConstants.TenantId), "not yet").Value!;

        await using var db = Create(dbName);
        db.TaskItems.Add(task);
        Assert.IsTrue(task.TransitionStatus(TaskItemStatus.InProgress).IsSuccess);

        // Two events are buffered on the aggregate and no row exists: the interceptor, not the raise, writes.
        Assert.AreEqual(2, task.DomainEvents.Count);
        Assert.AreEqual(0, await db.OutboxMessages.CountAsync(ct));

        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
        Assert.AreEqual(2, await db.OutboxMessages.CountAsync(ct));

        // The InMemory provider has no transaction, so "a rolled-back save leaves no row" cannot be proven
        // here at all - it is asserted against both real providers in
        // Test.Integration OutboxClaimTests.RolledBackSave_LeavesNoOutboxRow.
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task StagedPayload_RoundTripsThroughTheEnvelope()
    {
        var ct = TestContext.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var tenantId = DomainId.From<TenantId>(TestConstants.TenantId);
        var task = TaskItem.Create(tenantId, "round trip").Value!;

        await using var db = Create(dbName);
        db.TaskItems.Add(task);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);

        var row = await db.OutboxMessages.SingleAsync(ct);
        var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope>(row.Payload);

        Assert.IsNotNull(envelope);
        Assert.AreEqual(row.Id, envelope.Id, "the row id IS the broker MessageId");
        Assert.AreEqual(nameof(TaskItemCreatedEvent), envelope.Type);
        Assert.AreEqual(1, envelope.Version);
        Assert.AreEqual(TestConstants.TenantId, envelope.TenantId);
        Assert.AreEqual(Now, envelope.OccurredAtUtc);

        var payload = envelope.Payload.Deserialize<TaskItemCreatedEvent>();
        Assert.IsNotNull(payload);
        Assert.AreEqual(task.Id.Value, payload.TaskItemId);
        Assert.AreEqual(TestConstants.TenantId, payload.TenantId);
        Assert.AreEqual("round trip", payload.Title);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void UnknownEventType_IsRejectedBeforeAConsumerSeesIt()
    {
        Assert.IsTrue(IntegrationEventEnvelope.IsKnownType(nameof(TaskItemCreatedEvent)));
        Assert.IsFalse(IntegrationEventEnvelope.IsKnownType("SomeFutureEvent"));
        Assert.AreEqual(1, IntegrationEventEnvelope.VersionFor(nameof(TaskItemCreatedEvent)));
    }

    public TestContext TestContext { get; set; } = null!;

    private static TaskFlowDbContextTrxn Create(string dbName) =>
        new(new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(
                new VersionTimestampInterceptor(new FixedClock(Now)),
                new OutboxStagingInterceptor(new FixedClock(Now)))
            .Options)
        {
            AuditId = "outbox-staging-test",
            TenantId = TestConstants.TenantId
        };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
