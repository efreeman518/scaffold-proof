namespace TaskFlow.Domain.Shared;

/// <summary>
/// A lifecycle event raised by a TaskFlow aggregate. It extends the package marker with the tenant that owns it
/// (D-026): the same record is the integration-event payload wrapped by <c>IntegrationEventEnvelope</c>, and the
/// outbox mapper copies <see cref="TenantId"/> onto the broker <c>TenantId</c> header.
/// <para>
/// <c>closed</c>: every event record derives from this type in this assembly, so a <c>switch</c> over it with no
/// discard arm is exhaustive and a new event fails the build wherever it must be handled (its payload version in
/// <c>TaskFlowIntegrationEvents</c>). <see cref="TenantId"/> is abstract so each positional record declares it in
/// its own parameter list: the property keeps its position in the record, and the serialized payload keeps the
/// property order it had before the base type existed.
/// </para>
/// </summary>
public closed record TenantDomainEvent : EF.Domain.Contracts.IDomainEvent
{
    /// <summary>Tenant the event belongs to; becomes the broker <c>TenantId</c> header.</summary>
    public abstract Guid TenantId { get; init; }
}
