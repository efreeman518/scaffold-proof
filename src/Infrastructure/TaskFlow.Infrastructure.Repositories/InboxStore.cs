using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// D-028/D-029: provider-neutral insert-if-absent through the package upsert (MERGE on SQL Server, ON
/// CONFLICT DO NOTHING on PostgreSQL). Runs immediately on the write context's connection so it shares the
/// consumer's ambient transaction when one is open.
/// </summary>
public sealed class InboxStore(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null)
    : RepositoryBase<TaskFlowDbContextTrxn, string, Guid?>(db), IInboxStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> TryClaimAsync(string consumer, Guid messageId, CancellationToken ct = default)
    {
        // No whenMatched: a second delivery of the same message leaves the existing claim row alone and
        // reports zero rows, which is what tells the caller another consumer already owns this message.
        var inserted = await UpsertAsync(
                new ConsumerInbox
                {
                    Consumer = consumer,
                    MessageId = messageId,
                    ProcessedAtUtc = _timeProvider.GetUtcNow()
                },
                x => new { x.Consumer, x.MessageId },
                cancellationToken: ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        return inserted > 0;
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string consumer, Guid messageId, CancellationToken ct = default) =>
        DB.ConsumerInbox
            .Where(x => x.Consumer == consumer && x.MessageId == messageId)
            .ExecuteDeleteAsync(ct);

    /// <inheritdoc />
    // Batched so one sweep of a busy inbox cannot lock the table for the whole window. MessageId alone is the
    // batch key (EF cannot translate a tuple IN); the cutoff predicate stays on the delete, so a second
    // consumer's row for the same message is only removed when it is itself past the cutoff.
    public Task<int> PurgeProcessedAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
        DB.ConsumerInbox.ExecuteDeleteBatchedAsync(
            x => x.ProcessedAtUtc < cutoffUtc,
            x => x.MessageId,
            ct: ct);
}
