using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

// temporary: S6 owns the real IOutboxStaging implementation (staged beside OutboxStagingInterceptor);
// delete this file on merge and keep the messaging slice's version.
/// <summary>
/// Adds the outbox row to the write context's change tracker so it commits with the same
/// <c>SaveChangesAsync</c> as the data change it describes.
/// </summary>
public sealed class OutboxStaging(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null) : IOutboxStaging
{
    /// <summary>Service Bus topic / RabbitMQ exchange the dispatcher sends domain events to.</summary>
    public const string DomainEventsDestination = "taskflow-integration-events";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public void Stage(IntegrationEventEnvelope envelope, Guid? deterministicId = null)
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = deterministicId ?? envelope.Id,
            TenantId = envelope.TenantId,
            AvailableAtUtc = _timeProvider.GetUtcNow(),
            Destination = DomainEventsDestination,
            EventType = envelope.Type,
            EventVersion = envelope.Version,
            Payload = JsonSerializer.Serialize(envelope),
            CorrelationId = envelope.CorrelationId,
            OccurredAtUtc = envelope.OccurredAtUtc
        });
    }
}
