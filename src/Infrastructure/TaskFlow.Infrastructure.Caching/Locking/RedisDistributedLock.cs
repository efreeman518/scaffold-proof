using StackExchange.Redis;
using TaskFlow.Application.Contracts.Locking;

namespace TaskFlow.Infrastructure.Caching.Locking;

/// <summary>
/// Redis <c>SET key token NX PX</c> lock with a compare-and-delete release (D-052).
/// <para>
/// Ceiling: single node, no RedLock quorum. A failover that loses the last writes can hand the same lock to
/// two holders. That is acceptable for the startup tasks this covers - they are idempotent, so the lock is
/// there to avoid concurrent conflicting work, not to guarantee exactly-once. Upgrade path when a caller
/// needs a real mutual-exclusion guarantee: RedLock across independent Redis nodes, or move the lock into
/// the database that owns the data being protected.
/// </para>
/// <para>
/// Alternative considered (D-030): PostgreSQL <c>pg_try_advisory_lock</c>, which is a genuine
/// single-source-of-truth lock with automatic release on disconnect. Rejected as the default because it is
/// provider-specific and the dual-provider rule forbids a PostgreSQL-only code path here; Redis is present
/// on both provider configurations.
/// </para>
/// </summary>
public sealed class RedisDistributedLock : IDistributedLock, IDisposable
{
    /// <summary>
    /// Release must compare the token first. A plain <c>DEL</c> lets a holder whose ttl already expired
    /// delete the lock the next holder has since taken, which is exactly the double-run the lock prevents.
    /// </summary>
    private const string ReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly Lazy<IConnectionMultiplexer> _redis;

    /// <summary>Initializes the lock, connecting lazily so startup does not block on Redis.</summary>
    /// <param name="connectionString">StackExchange.Redis configuration string.</param>
    public RedisDistributedLock(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _redis = new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(connectionString));
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(
        string key, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        // Random per acquisition: the token is the proof of ownership the release compares against.
        var token = Guid.NewGuid().ToString("N");
        var database = _redis.Value.GetDatabase();

        return await database.StringSetAsync(key, token, ttl, When.NotExists).ConfigureAwait(false)
            ? new Handle(database, key, token)
            : null;
    }

    /// <summary>Closes the connection this lock opened.</summary>
    public void Dispose()
    {
        if (_redis.IsValueCreated) _redis.Value.Dispose();
    }

    private sealed class Handle(IDatabase database, string key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await database.ScriptEvaluateAsync(ReleaseScript, [key], [token]).ConfigureAwait(false);
    }
}
