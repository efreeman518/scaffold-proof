using EF.Domain.Contracts;
using EF.Messaging.Outbox;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// D-026: turns a raised domain event into its outbox entry inside <c>SaveChanges</c>. The envelope is TaskFlow's
/// (generated serializer, per-type version); the tenant rides as the <see cref="TaskFlowIntegrationEvents.TenantIdHeader"/>
/// header. An event without a tenant fails the save rather than being published unscoped.
/// </summary>
public sealed class TaskFlowOutboxEventMapper : IOutboxEventMapper
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The event does not derive from <see cref="TenantDomainEvent"/>.</exception>
    public OutboxEntry Map(IDomainEvent domainEvent, DateTimeOffset occurredAtUtc, string? correlationId)
    {
        // IDomainEvent is the package's open marker, so this one type test stays; past it the event is a closed
        // TenantDomainEvent, which TaskFlowIntegrationEvents switches over exhaustively.
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent is not TenantDomainEvent tenantEvent)
            throw new InvalidOperationException(
                $"{domainEvent.GetType().Name} does not derive from {nameof(TenantDomainEvent)}; every TaskFlow event carries its tenant.");

        return TaskFlowIntegrationEvents.Entry(
            TaskFlowIntegrationEvents.Envelope(tenantEvent, occurredAtUtc, correlationId), tenantEvent.TenantId);
    }
}
