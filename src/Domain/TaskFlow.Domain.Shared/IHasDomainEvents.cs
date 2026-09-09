namespace TaskFlow.Domain.Shared;

/// <summary>
/// An aggregate that accumulates domain events until the persistence layer stages them (D-026).
/// The outbox staging interceptor drains this in the same <c>SaveChangesAsync</c> as the domain write,
/// so no call site publishes anything.
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>Events raised since the last drain, in the order they were raised.</summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>Drops the accumulated events; called by the staging interceptor after it creates the rows.</summary>
    void ClearDomainEvents();
}
