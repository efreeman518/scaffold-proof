using EF.Cache;

namespace TaskFlow.Application.Contracts.Caching;

/// <summary>
/// The snapshots TaskFlow caches. Each kind is an immutable projection, never an entity: a cached entity
/// would come back detached, stale, and with a Version that lies about the row it came from.
/// <para>
/// The name is the <see cref="CacheKey.Category"/> segment of every rendered key, so renaming a member
/// retires the entries under the old name rather than reading them.
/// </para>
/// </summary>
public enum CacheKind
{
    /// <summary>Tenant task counts by status, overdue, and total.</summary>
    TaskSummary,

    /// <summary>Tenant category and tag lists for pickers.</summary>
    TaskMetadata
}

/// <summary>
/// Names of the durability profiles configured in <c>CacheSettings:Profiles</c>. Metadata changes rarely and
/// is expensive to rebuild, so it is held long and refreshed early; a summary is cheap and visibly wrong when
/// stale, so it is held for seconds with a soft timeout that returns the previous value instead of making the
/// caller wait on a slow aggregate.
/// </summary>
public static class CacheProfiles
{
    public const string Metadata = "Metadata";
    public const string Summary = "Summary";
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
/// TaskFlow's key and tag vocabulary over <see cref="ITypedCache"/>. The package owns durability, the
/// distributed tier, the backplane, fail-safe and the serializer; what has to stay here is the mapping from a
/// snapshot kind to a key and to the tag set the entry depends on, so callers never construct either and
/// cannot drift from each other.
/// </summary>
public static class TaskFlowCache
{
    /// <summary>
    /// Identity of one cached snapshot: the kind is the category, the tenant is the id, and entries are never
    /// shared across tenants.
    /// </summary>
    /// <param name="kind">Which snapshot.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="discriminator">Optional variant (a filter or projection flavor) within the kind.</param>
    public static CacheKey Key(CacheKind kind, Guid tenantId, string? discriminator = null) =>
        new(kind.ToString(), tenantId.ToString("N"), discriminator);

    /// <summary>
    /// The tags one entry carries: its tenant, plus every entity type whose mutation invalidates it. Writers
    /// evict by what they changed, so a new snapshot only has to declare what it is built from here.
    /// </summary>
    /// <param name="kind">Which snapshot.</param>
    /// <param name="tenantId">Owning tenant.</param>
    public static IReadOnlyList<string> TagsFor(CacheKind kind, Guid tenantId) => kind switch
    {
        CacheKind.TaskSummary =>
            [CacheTags.Tenant(tenantId), CacheTags.Entity(tenantId, CacheTags.TaskItem)],
        CacheKind.TaskMetadata =>
            [
                CacheTags.Tenant(tenantId),
                CacheTags.Entity(tenantId, CacheTags.Category),
                CacheTags.Entity(tenantId, CacheTags.Tag)
            ],
        _ => [CacheTags.Tenant(tenantId)]
    };

    /// <summary>
    /// Returns the cached snapshot or builds it with <paramref name="factory"/>. The entry is tagged for the
    /// tenant and for every entity type the snapshot depends on, so writers can evict it without knowing it exists.
    /// </summary>
    /// <typeparam name="T">
    /// The cached snapshot type. It must be public: the MessagePack arm's contractless resolver emits its
    /// formatter at run time and cannot reach an internal type (an architecture test pins this).
    /// </typeparam>
    /// <param name="cache">The cache instance.</param>
    /// <param name="kind">Which snapshot.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="factory">Builds the snapshot on a miss.</param>
    /// <param name="profile">Durability profile name from <see cref="CacheProfiles"/>.</param>
    /// <param name="discriminator">Optional variant within the kind.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<T> GetOrSetAsync<T>(
        this ITypedCache cache,
        CacheKind kind,
        Guid tenantId,
        Func<CancellationToken, Task<T>> factory,
        string profile,
        string? discriminator = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return cache.GetOrSetAsync(
            Key(kind, tenantId, discriminator), factory, profile, TagsFor(kind, tenantId), ct);
    }
}
