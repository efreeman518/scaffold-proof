using TaskFlow.Domain.Shared;

namespace TaskFlow.Domain.Model;

/// <summary>
/// Per-aggregate buffer of raised domain events (D-026). Held as a field rather than a base-class member so
/// entities that raise nothing pay nothing and EF's parameterless materialization needs no special handling.
/// </summary>
public sealed class DomainEventContainer
{
    private readonly List<IDomainEvent> _events = [];

    /// <summary>Events raised since the last <see cref="Clear"/>, in order.</summary>
    public IReadOnlyCollection<IDomainEvent> Events => _events;

    /// <summary>Appends one event to the buffer.</summary>
    public void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);

    /// <summary>Empties the buffer.</summary>
    public void Clear() => _events.Clear();
}
