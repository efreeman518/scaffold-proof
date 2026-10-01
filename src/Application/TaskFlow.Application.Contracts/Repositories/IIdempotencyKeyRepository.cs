namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>
/// Maps an <c>Idempotency-Key</c> header to the id its write is sent with (D-074). The mapping is committed in its
/// own save before the write runs, so every attempt of one logical request gets the same id.
/// </summary>
public interface IIdempotencyKeyRepository
{
    /// <summary>
    /// Returns the entity id mapped to (<paramref name="tenantId"/>, <paramref name="scope"/>, <paramref name="key"/>).
    /// When none exists, stores a new UUIDv7 mapping and returns it; when a concurrent request stored the same key
    /// first, returns that request's id.
    /// </summary>
    Task<Guid> GetOrAddEntityIdAsync(Guid tenantId, string scope, string key, CancellationToken ct = default);

    /// <summary>
    /// <see cref="GetOrAddEntityIdAsync"/> for a child add on <paramref name="taskItemId"/>: a new mapping is stored only
    /// when that task exists and the context tenant can see it (a tenant-filtered existence read before the insert).
    /// Returns <c>null</c>, and stores nothing, when it cannot, so the add answers its usual 404.
    /// </summary>
    Task<Guid?> GetOrAddChildEntityIdAsync(Guid tenantId, string scope, string key, Guid taskItemId, CancellationToken ct = default);

    /// <summary>Deletes mappings created before <paramref name="cutoffUtc"/>. Returns the number of rows deleted.</summary>
    Task<int> PurgeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}
