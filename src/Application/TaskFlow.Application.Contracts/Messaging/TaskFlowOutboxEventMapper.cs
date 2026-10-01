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
    /// <exception cref="InvalidOperationException">The event does not implement <see cref="ITenantDomainEvent"/>.</exception>
    public OutboxEntry Map(IDomainEvent domainEvent, DateTimeOffset occurredAtUtc, string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent is not ITenantDomainEvent tenantEvent)
            throw new InvalidOperationException(
                $"{domainEvent.GetType().Name} does not implement {nameof(ITenantDomainEvent)}; every TaskFlow event carries its tenant.");

        return TaskFlowIntegrationEvents.Entry(
            TaskFlowIntegrationEvents.Envelope(tenantEvent, occurredAtUtc, correlationId), tenantEvent.TenantId);
    }
}
