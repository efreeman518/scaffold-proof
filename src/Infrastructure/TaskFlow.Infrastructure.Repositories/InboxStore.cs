using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// D-028/D-029 two-state inbox. The claim is an insert-if-absent through the package upsert (MERGE on SQL Server,
/// ON CONFLICT DO NOTHING on PostgreSQL), then a conditional takeover of an expired in-progress claim; both are
/// single statements, so of two racing deliveries exactly one acquires. Every statement runs immediately on the
/// write context's connection so it shares the consumer's ambient transaction when one is open.
/// </summary>
public sealed class InboxStore(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null)
    : RepositoryBase<TaskFlowDbContextTrxn, string, Guid?>(db), IInboxStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<InboxClaim> TryClaimAsync(
        string consumer, Guid messageId, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        // Two passes at most: a claim released or purged between the takeover and the read leaves no row, and
        // the insert is then worth one more try. A second miss is reported as in progress, which retries later.
        for (var pass = 0; pass < 2; pass++)
        {
            var token = Guid.CreateVersion7();
            var now = _timeProvider.GetUtcNow();
            var expires = now + leaseDuration;

            // No whenMatched: an existing claim, in any state, is left alone and reports zero rows.
            var inserted = await UpsertAsync(
                    new ConsumerInbox
                    {
                        Consumer = consumer,
                        MessageId = messageId,
                        ClaimToken = token,
                        ClaimedAtUtc = now,
                        LeaseExpiresUtc = expires
                    },
                    x => new { x.Consumer, x.MessageId },
                    cancellationToken: ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);
            if (inserted > 0) return new InboxClaim(InboxClaimStatus.Acquired, token);

            // An in-progress claim whose lease ran out belongs to a delivery that crashed or hung: take it over.
            // The predicate is re-evaluated under the row lock, so two racing redeliveries cannot both win.
            var takenOver = await DB.ConsumerInbox
                .Where(x => x.Consumer == consumer && x.MessageId == messageId
                            && x.CompletedAtUtc == null && x.LeaseExpiresUtc < now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.ClaimToken, token)
                    .SetProperty(x => x.ClaimedAtUtc, now)
                    .SetProperty(x => x.LeaseExpiresUtc, expires), ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);
            if (takenOver > 0) return new InboxClaim(InboxClaimStatus.Acquired, token);

            var existing = await DB.ConsumerInbox.AsNoTracking()
                .Where(x => x.Consumer == consumer && x.MessageId == messageId)
                .Select(x => new { x.CompletedAtUtc })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);
            if (existing is null) continue;

            return new InboxClaim(
                existing.CompletedAtUtc is null ? InboxClaimStatus.InProgress : InboxClaimStatus.Duplicate,
                Guid.Empty);
        }

        return new InboxClaim(InboxClaimStatus.InProgress, Guid.Empty);
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(
        string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var updated = await DB.ConsumerInbox
            .Where(x => x.Consumer == consumer && x.MessageId == messageId
                        && x.ClaimToken == claimToken && x.CompletedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CompletedAtUtc, now)
                .SetProperty(x => x.LeaseExpiresUtc, (DateTimeOffset?)null), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(
        string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default)
    {
        var deleted = await DB.ConsumerInbox
            .Where(x => x.Consumer == consumer && x.MessageId == messageId
                        && x.ClaimToken == claimToken && x.CompletedAtUtc == null)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return deleted > 0;
    }

    /// <inheritdoc />
    // Batched so one sweep of a busy inbox cannot lock the table for the whole window. ClaimToken is the batch
    // key because BatchedExecute needs a key that is unique on its own and EF cannot translate a tuple IN; every
    // claim mints a fresh token, and the migration backfilled a distinct one for each pre-existing row.
    public Task<int> PurgeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
        DB.ConsumerInbox.ExecuteDeleteBatchedAsync(
            x => x.CompletedAtUtc < cutoffUtc || (x.CompletedAtUtc == null && x.LeaseExpiresUtc < cutoffUtc),
            x => x.ClaimToken,
            ct: ct);
}
