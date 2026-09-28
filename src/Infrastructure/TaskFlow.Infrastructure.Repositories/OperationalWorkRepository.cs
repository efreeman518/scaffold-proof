using EF.Data.Contracts;
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
    private const int MaxOwnerLength = 128;

    /// <summary>LastError written when a row is parked because its final attempt never settled.</summary>
    public const string LeaseExpiredOnFinalAttempt = "Lease expired on final attempt";
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<LeasedBatch<TWork>> ClaimAsync<TWork>(
        int batchSize, TimeSpan leaseDuration, int maxAttempts, string owner, CancellationToken ct)
        where TWork : OperationalWorkBase
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var now = _timeProvider.GetUtcNow();
        var set = db.Set<TWork>();

        // A row that used its last attempt and was never settled - the worker crashed, or the send hung past the
        // lease - would otherwise be re-leased forever and re-sent on every expiry. Park it once its lease is gone.
        await set
            .Where(w => w.DeadLetteredAtUtc == null
                        && w.AttemptCount >= maxAttempts
                        && w.AvailableAtUtc <= now
                        && (w.LeaseExpiresUtc == null || w.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.DeadLetteredAtUtc, now)
                .SetProperty(w => w.LeaseToken, (Guid?)null)
                .SetProperty(w => w.LeaseOwner, (string?)null)
                .SetProperty(w => w.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(w => w.LastError, w => w.LastError ?? LeaseExpiredOnFinalAttempt), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        var candidates = await Claimable(set, now, maxAttempts)
            .OrderBy(w => w.AvailableAtUtc).ThenBy(w => w.Id)
            .Select(w => w.Id)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        if (candidates.Count == 0) return LeasedBatch<TWork>.Empty;

        var token = Guid.CreateVersion7();
        var expires = now + leaseDuration;
        var leaseOwner = Truncate(owner, MaxOwnerLength);
        // Same predicate as the candidate read: a replica that lost the race updates nothing.
        var claimed = await Claimable(set, now, maxAttempts)
            .Where(w => candidates.Contains(w.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.LeaseToken, token)
                .SetProperty(w => w.LeaseOwner, leaseOwner)
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
    public async Task<bool> ReleaseAsync<TWork>(Guid leaseToken, Guid id, int attemptCount, string error, CancellationToken ct)
        where TWork : OperationalWorkBase
    {
        var availableAt = _timeProvider.GetUtcNow() + Backoff(attemptCount);
        var reason = Truncate(error);
        var updated = await db.Set<TWork>()
            .Where(w => w.Id == id && w.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.LeaseToken, (Guid?)null)
                .SetProperty(w => w.LeaseOwner, (string?)null)
                .SetProperty(w => w.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(w => w.AvailableAtUtc, availableAt)
                .SetProperty(w => w.LastError, reason), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeadLetterAsync<TWork>(Guid leaseToken, Guid id, string error, CancellationToken ct)
        where TWork : OperationalWorkBase
    {
        // Poison: park the row and keep it. It is the only surviving copy of the event; OutboxRetention
        // deletes it after 7 days and the admin retry endpoint resets it.
        var now = _timeProvider.GetUtcNow();
        var reason = Truncate(error);
        var updated = await db.Set<TWork>()
            .Where(w => w.Id == id && w.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.DeadLetteredAtUtc, now)
                .SetProperty(w => w.LeaseToken, (Guid?)null)
                .SetProperty(w => w.LeaseOwner, (string?)null)
                .SetProperty(w => w.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(w => w.LastError, reason), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<int> AbandonAsync<TWork>(Guid leaseToken, IReadOnlyCollection<Guid> ids, CancellationToken ct)
        where TWork : OperationalWorkBase
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return 0;

        var now = _timeProvider.GetUtcNow();
        var idList = ids as IList<Guid> ?? [.. ids];
        return await db.Set<TWork>()
            .Where(w => w.LeaseToken == leaseToken && idList.Contains(w.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.LeaseToken, (Guid?)null)
                .SetProperty(w => w.LeaseOwner, (string?)null)
                .SetProperty(w => w.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(w => w.AvailableAtUtc, now)
                .SetProperty(w => w.AttemptCount, w => w.AttemptCount > 0 ? w.AttemptCount - 1 : 0), ct)
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
        db.Set<TWork>().ExecuteDeleteBatchedAsync(
            w => w.DeadLetteredAtUtc != null && w.DeadLetteredAtUtc < cutoffUtc, w => w.Id, ct: ct);

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

    /// <summary>
    /// Rows that are due, have attempts left, and whose lease is free or expired. One expression, used by both
    /// claim steps.
    /// </summary>
    private static IQueryable<TWork> Claimable<TWork>(IQueryable<TWork> set, DateTimeOffset now, int maxAttempts)
        where TWork : OperationalWorkBase
        => set.Where(w => w.DeadLetteredAtUtc == null
                          && w.AvailableAtUtc <= now
                          && w.AttemptCount < maxAttempts
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
    public static string Truncate(string error) => Truncate(error, MaxErrorLength);

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= maxLength ? value : value[..maxLength];
}
