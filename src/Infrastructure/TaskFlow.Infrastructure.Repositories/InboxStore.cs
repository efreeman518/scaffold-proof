using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// D-028/D-029: provider-neutral insert-if-absent through FlexLabs Upsert (MERGE on SQL Server, ON CONFLICT DO
/// NOTHING on PostgreSQL). Runs immediately on the write context's connection so it shares the consumer's
/// ambient transaction when one is open.
/// fallback: replace with EF.Data IRepositoryBase.UpsertAsync when published (package request 7).
/// </summary>
public sealed class InboxStore(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null) : IInboxStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> TryClaimAsync(string consumer, Guid messageId, CancellationToken ct = default)
    {
        var inserted = await db.ConsumerInbox
            .Upsert(new ConsumerInbox
            {
                Consumer = consumer,
                MessageId = messageId,
                ProcessedAtUtc = _timeProvider.GetUtcNow()
            })
            .On(x => new { x.Consumer, x.MessageId })
            .NoUpdate()
            .RunAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        return inserted > 0;
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string consumer, Guid messageId, CancellationToken ct = default) =>
        db.ConsumerInbox
            .Where(x => x.Consumer == consumer && x.MessageId == messageId)
            .ExecuteDeleteAsync(ct);
}
