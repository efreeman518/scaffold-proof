namespace TaskFlow.Application.Contracts.Caching;

/// <summary>
/// The snapshots TaskFlow caches. Each kind is an immutable projection, never an entity: a cached entity
/// would come back detached, stale, and with a Version that lies about the row it came from.
/// </summary>
public enum CacheKind
{
    /// <summary>Tenant task counts by status, overdue, and total.</summary>
    TaskSummary,

    /// <summary>Tenant category and tag lists for pickers.</summary>
    TaskMetadata
}

/// <summary>
/// Identity of one cache entry. Rendered as <c>{env}:{schemaVersion}:{tenantId:N}:{kind}[:{discriminator}]</c>:
/// the environment keeps deployments apart on a shared Redis, and the schema version retires every entry at once
/// when a snapshot's shape changes, which is cheaper and safer than hunting for entries to evict.
/// </summary>
/// <param name="Kind">Which snapshot.</param>
/// <param name="TenantId">Owning tenant; entries are never shared across tenants.</param>
/// <param name="Discriminator">Optional variant (a filter or projection flavor) within the kind.</param>
public readonly record struct CacheKey(CacheKind Kind, Guid TenantId, string? Discriminator = null);

/// <summary>
/// Named durability profile. Metadata changes rarely and is expensive to rebuild, so it is held long and
/// refreshed early; a summary is cheap and visibly wrong when stale, so it is held for seconds with a soft
/// timeout that returns the previous value instead of making the caller wait on a slow aggregate.
/// </summary>
public enum CacheProfile
{
    Metadata,
    Summary
}

/// <summary>
/// Tag vocabulary for invalidation. Writes evict by tag rather than by key: a writer knows what it changed
/// (a category, a task), not which snapshots happen to include it.
/// </summary>
public static class CacheTags
{
    public const string TaskItem = "taskitem";
    public const string Category = "category";
    public const string Tag = "tag";

    /// <summary>Everything cached for one tenant.</summary>
    public static string Tenant(Guid tenantId) => $"t:{tenantId:N}";

    /// <summary>Everything cached for one tenant that depends on one entity type.</summary>
    public static string Entity(Guid tenantId, string entity) => $"t:{tenantId:N}:{entity}";
}

/// <summary>
/// TaskFlow's cache boundary. The application layer states what it wants cached and for how long; the
/// distributed cache, the backplane, fail-safe, and the degraded-mode telemetry stay behind this interface,
/// so no service or handler ever holds a FusionCache type (an architecture test enforces that).
/// </summary>
public interface ITaskFlowCache
{
    /// <summary>
    /// Returns the cached snapshot or builds it with <paramref name="factory"/>. The entry is tagged for the
    /// tenant and for every entity type the snapshot depends on, so writers can evict it without knowing it exists.
    /// </summary>
    Task<T> GetOrSetAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        CacheProfile profile,
        CancellationToken ct = default);

    /// <summary>Evicts every entry carrying <paramref name="tag"/>, across replicas via the backplane.</summary>
    Task RemoveByTagAsync(string tag, CancellationToken ct = default);

    /// <summary>Evicts one entry.</summary>
    Task RemoveAsync(CacheKey key, CancellationToken ct = default);
}
