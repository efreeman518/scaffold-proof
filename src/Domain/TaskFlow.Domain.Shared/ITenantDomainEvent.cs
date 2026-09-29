namespace TaskFlow.Domain.Shared;

/// <summary>
/// A lifecycle event raised by a TaskFlow aggregate. It extends the package marker with the tenant that owns it
/// (D-026): the same record is the integration-event payload wrapped by <c>IntegrationEventEnvelope</c>, and the
/// outbox mapper copies <see cref="TenantId"/> onto the broker <c>TenantId</c> header.
/// </summary>
public interface ITenantDomainEvent : EF.Domain.Contracts.IDomainEvent
{
    /// <summary>Tenant the event belongs to; becomes the broker <c>TenantId</c> header.</summary>
    Guid TenantId { get; }
}
