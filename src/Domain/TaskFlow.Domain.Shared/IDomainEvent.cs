namespace TaskFlow.Domain.Shared;

/// <summary>
/// A lifecycle event raised by an aggregate. The same record is the integration-event payload wrapped by
/// <c>IntegrationEventEnvelope</c>, so every event carries the tenant that owns it (D-026).
/// </summary>
public interface IDomainEvent
{
    /// <summary>Tenant the event belongs to; becomes the envelope and broker <c>TenantId</c> property.</summary>
    Guid TenantId { get; }
}
