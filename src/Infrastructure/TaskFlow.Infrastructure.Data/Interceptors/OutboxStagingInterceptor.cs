using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Diagnostics;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Infrastructure.Data.Interceptors;

/// <summary>
/// D-026: drains every tracked aggregate's raised domain events into <see cref="OutboxMessage"/> rows inside the
/// same <c>SaveChanges</c>, so the event and the domain change commit or roll back together and no call site
/// publishes anything. Rows added here are part of the change tracker EF is already saving.
/// </summary>
public sealed class OutboxStagingInterceptor(TimeProvider? timeProvider = null, MessagingMetrics? metrics = null) : SaveChangesInterceptor
{
    /// <summary>Logical channel every TaskFlow integration event goes to; the transport maps it to a topic or exchange.</summary>
    public const string DefaultDestination = "DomainEvents";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stage(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stage(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stage(DbContext? context)
    {
        if (context is null) return;

        var raisers = context.ChangeTracker.Entries<IHasDomainEvents>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();
        if (raisers.Count == 0) return;

        var now = _timeProvider.GetUtcNow();
        var staged = 0;
        // No request context is reachable from a pooled-factory interceptor; the ambient Activity is the same
        // correlation the rest of the pipeline emits, and is null outside a traced operation.
        var correlationId = Activity.Current?.Id;

        foreach (var raiser in raisers)
        {
            foreach (var domainEvent in raiser.DomainEvents)
            {
                var envelope = IntegrationEventEnvelope.From(domainEvent, now, correlationId);
                context.Add(ToRow(envelope, now));
                staged++;
            }

            raiser.ClearDomainEvents();
        }

        metrics?.RecordStaged(staged);
    }

    /// <summary>Maps an envelope to its outbox row; the row id IS the MessageId so replay is detectable.</summary>
    public static OutboxMessage ToRow(IntegrationEventEnvelope envelope, DateTimeOffset availableAtUtc) => new()
    {
        Id = envelope.Id,
        TenantId = envelope.TenantId,
        AvailableAtUtc = availableAtUtc,
        Destination = DefaultDestination,
        EventType = envelope.Type,
        EventVersion = envelope.Version,
        // D-048: generated metadata. This runs inside SaveChanges on every write that raised an event,
        // so it is the hottest envelope serialization in the app.
        Payload = System.Text.Json.JsonSerializer.Serialize(
            envelope, TaskFlowMessagingJsonContext.Default.IntegrationEventEnvelope),
        CorrelationId = envelope.CorrelationId,
        OccurredAtUtc = envelope.OccurredAtUtc
    };
}
