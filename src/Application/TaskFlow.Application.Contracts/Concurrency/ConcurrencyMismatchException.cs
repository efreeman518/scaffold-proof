namespace TaskFlow.Application.Contracts.Concurrency;

/// <summary>
/// Thrown when an If-Match version does not match the stored aggregate version (D-032 -> HTTP 412).
/// Derives from <see cref="Exception"/>, not <see cref="InvalidOperationException"/>, because the
/// global handler maps that base to 400 - inheriting it would silently downgrade every 412.
/// </summary>
public sealed class ConcurrencyMismatchException(string entityType, Guid entityId, long? expected, long current)
    : Exception($"{entityType} {entityId} was modified by another request (expected version {expected?.ToString() ?? "none"}, current {current}).")
{
    public string EntityType { get; } = entityType;
    public Guid EntityId { get; } = entityId;
    public long? Expected { get; } = expected;
    public long Current { get; } = current;
}
