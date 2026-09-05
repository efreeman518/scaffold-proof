using Microsoft.EntityFrameworkCore;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// D-026 provider-neutral three-step claim. (a) read candidate ids, (b) claim them with a conditional
/// <c>ExecuteUpdateAsync</c> that re-asserts the free-lease predicate, (c) read back by lease token. A racing
/// replica simply claims fewer rows; nothing is keyed on a timestamp, whose rounding differs per provider.
///
/// High-throughput upgrade path, deliberately NOT taken here (D-030 keeps one code path):
///   SQL Server: UPDATE TOP(@n) w WITH (UPDLOCK, READPAST, ROWLOCK)
///                 SET LeaseToken = @t, LeaseOwner = @o, LeaseExpiresUtc = @e, AttemptCount = AttemptCount + 1
///                 OUTPUT inserted.* FROM taskflow.OutboxMessage w WHERE ...;
///   PostgreSQL: UPDATE taskflow."OutboxMessage" SET ... WHERE "Id" IN (
///                 SELECT "Id" FROM taskflow."OutboxMessage" WHERE ... ORDER BY ... LIMIT @n FOR UPDATE SKIP LOCKED)
///               RETURNING *;
/// Both collapse the three round trips into one statement and remove the lost-race read; both are provider SQL.
/// </summary>
public sealed class OperationalWorkRepository(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null)
    : IOperationalWorkRepository
{
    private const int MaxErrorLength = 1024;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<LeasedBatch<TWork>> ClaimAsync<TWork>(
        int batchSize, TimeSpan leaseDuration, string owner, CancellationToken ct)
        where TWork : OperationalWorkBase
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var now = _timeProvider.GetUtcNow();
        var set = db.Set<TWork>();

        var candidates = await Claimable(set, now)
            .OrderBy(w => w.AvailableAtUtc).ThenBy(w => w.Id)
            .Select(w => w.Id)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        if (candidates.Count == 0) return LeasedBatch<TWork>.Empty;

        var token = Guid.CreateVersion7();
        var expires = now + leaseDuration;
        // Same predicate as the candidate read: a replica that lost the race updates nothing.
        var claimed = await Claimable(set, now)
            .Where(w => candidates.Contains(w.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.LeaseToken, token)
                .SetProperty(w => w.LeaseOwner, owner)
                .SetProperty(w => w.LeaseExpiresUtc, expires)
                .SetProperty(w => w.AttemptCount, w => w.AttemptCount + 1), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        if (claimed == 0) return LeasedBatch<TWork>.Empty;

        var items = await set.AsNoTracking()
            .Where(w => w.LeaseToken == token)
            .OrderBy(w => w.AvailableAtUtc).ThenBy(w => w.Id)
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        return new LeasedBatch<TWork>(token, items);
    }

    /// <inheritdoc />
    public async Task<int> CompleteAsync<TWork>(Guid leaseToken, IReadOnlyCollection<Guid> ids, CancellationToken ct)
        where TWork : OperationalWorkBase
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return 0;

        var idList = ids as IList<Guid> ?? [.. ids];
        return await db.Set<TWork>()
            .Where(w => w.LeaseToken == leaseToken && idList.Contains(w.Id))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync<TWork>(Guid leaseToken, Guid id, int attemptCount, string error, CancellationToken ct)
        where TWork : OperationalWorkBase
    {
        var now = _timeProvider.GetUtcNow();
        var reason = Truncate(error);
        var owned = db.Set<TWork>().Where(w => w.Id == id && w.LeaseToken == leaseToken);

        if (attemptCount >= OperationalWorkBase.MaxAttempts)
        {
            // Poison: park the row and keep it. It is the only surviving copy of the event; OutboxRetention
            // deletes it after 7 days and the admin retry endpoint resets it.
            await owned.ExecuteUpdateAsync(s => s
                .SetProperty(w => w.DeadLetteredAtUtc, now)
                .SetProperty(w => w.LeaseToken, (Guid?)null)
                .SetProperty(w => w.LeaseOwner, (string?)null)
                .SetProperty(w => w.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(w => w.LastError, reason), ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);
            return;
        }

        var availableAt = now + Backoff(attemptCount);
        await owned.ExecuteUpdateAsync(s => s
            .SetProperty(w => w.LeaseToken, (Guid?)null)
            .SetProperty(w => w.LeaseOwner, (string?)null)
            .SetProperty(w => w.LeaseExpiresUtc, (DateTimeOffset?)null)
            .SetProperty(w => w.AvailableAtUtc, availableAt)
            .SetProperty(w => w.LastError, reason), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    public async Task<bool> RetryDeadLetteredAsync<TWork>(Guid id, CancellationToken ct)
        where TWork : OperationalWorkBase
    {
        var now = _timeProvider.GetUtcNow();
        var updated = await db.Set<TWork>()
            .Where(w => w.Id == id && w.DeadLetteredAtUtc != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.DeadLetteredAtUtc, (DateTimeOffset?)null)
                .SetProperty(w => w.AttemptCount, 0)
                .SetProperty(w => w.AvailableAtUtc, now)
                .SetProperty(w => w.LeaseToken, (Guid?)null)
                .SetProperty(w => w.LeaseOwner, (string?)null)
                .SetProperty(w => w.LeaseExpiresUtc, (DateTimeOffset?)null), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return updated > 0;
    }

    /// <inheritdoc />
    public Task<int> PurgeDeadLetteredAsync<TWork>(DateTimeOffset cutoffUtc, CancellationToken ct)
        where TWork : OperationalWorkBase =>
        db.Set<TWork>()
            .Where(w => w.DeadLetteredAtUtc != null && w.DeadLetteredAtUtc < cutoffUtc)
            .ExecuteDeleteBatchedAsync(w => w.Id, ct: ct);

    /// <inheritdoc />
    public async Task<OutboxBacklog> GetOutboxBacklogAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var live = db.OutboxMessages.AsNoTracking().Where(w => w.DeadLetteredAtUtc == null);

        var pending = await live.CountAsync(ct).ConfigureAwait(ConfigureAwaitOptions.None);
        var deadLettered = await db.OutboxMessages.AsNoTracking()
            .CountAsync(w => w.DeadLetteredAtUtc != null, ct).ConfigureAwait(ConfigureAwaitOptions.None);
        var oldest = await live.Where(w => w.AvailableAtUtc <= now)
            .MinAsync(w => (DateTimeOffset?)w.AvailableAtUtc, ct).ConfigureAwait(ConfigureAwaitOptions.None);

        var blobDeletePending = await db.BlobDeleteWork.AsNoTracking()
            .CountAsync(w => w.DeadLetteredAtUtc == null, ct).ConfigureAwait(ConfigureAwaitOptions.None);

        return new OutboxBacklog(
            pending, deadLettered, oldest is null ? TimeSpan.Zero : now - oldest.Value, blobDeletePending);
    }

    /// <summary>Rows that are due and whose lease is free or expired. One expression, used by both claim steps.</summary>
    private static IQueryable<TWork> Claimable<TWork>(IQueryable<TWork> set, DateTimeOffset now)
        where TWork : OperationalWorkBase
        => set.Where(w => w.DeadLetteredAtUtc == null
                          && w.AvailableAtUtc <= now
                          && (w.LeaseExpiresUtc == null || w.LeaseExpiresUtc < now));

    /// <summary>2s * 2^(attempt-1) capped at 5 minutes, with up to 20% positive jitter so replicas desynchronize.</summary>
    public static TimeSpan Backoff(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 30);
        var ticks = Math.Min(BaseBackoff.Ticks * (1L << exponent), MaxBackoff.Ticks);
        var jitter = (long)(ticks * 0.2 * Random.Shared.NextDouble());
        return TimeSpan.FromTicks(ticks + jitter);
    }

    /// <summary>Fits an exception message into the LastError column without throwing on a long one.</summary>
    public static string Truncate(string error)
        => string.IsNullOrEmpty(error) ? string.Empty
            : error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}
