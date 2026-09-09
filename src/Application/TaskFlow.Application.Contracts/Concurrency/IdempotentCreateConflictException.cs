namespace TaskFlow.Application.Contracts.Concurrency;

/// <summary>
/// Thrown when a caller-supplied id already exists with a materially different payload (D-033 -> HTTP 409).
/// An equivalent payload is a replay and returns the stored entity instead.
/// </summary>
public sealed class IdempotentCreateConflictException(string entityType, Guid entityId)
    : Exception($"A different {entityType} already exists with id {entityId}.")
{
    public string EntityType { get; } = entityType;
    public Guid EntityId { get; } = entityId;
}
