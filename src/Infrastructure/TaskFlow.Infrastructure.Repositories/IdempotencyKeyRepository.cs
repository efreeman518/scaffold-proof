using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Idempotency-key mappings on the write context (D-074). The table has no tenant query filter, so every read
/// names the tenant. The mapping save runs before the request's own write and saves only the mapping row.
/// </summary>
public sealed class IdempotencyKeyRepository(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null)
    : IIdempotencyKeyRepository
{
    /// <summary>Rows one purge statement deletes; the inbox purge's batch size.</summary>
    public const int PurgeBatchSize = BatchedExecute.DefaultBatchSize;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<Guid> GetOrAddEntityIdAsync(Guid tenantId, string scope, string key, CancellationToken ct = default)
    {
        // A retry finds its mapping here; only a first attempt or a concurrent duplicate inserts.
        if (await FindAsync(tenantId, scope, key, ct).ConfigureAwait(ConfigureAwaitOptions.None) is Guid stored)
            return stored;

        var now = _timeProvider.GetUtcNow();
        var record = new IdempotencyKeyRecord
        {
            EntityId = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Scope = scope,
            Key = key,
            CreatedUtc = now
        };
        db.IdempotencyKeys.Add(record);
        try
        {
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);
            return record.EntityId;
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Provider-neutral: the existence read decides, not the provider's error code. A concurrent request
            // stored the key first, so this request uses its id; any other write failure is rethrown.
            if (await FindAsync(tenantId, scope, key, ct).ConfigureAwait(ConfigureAwaitOptions.None) is Guid raced)
                return raced;
            throw;
        }
        finally
        {
            // The request's own write saves through this same context; it must not see the mapping row again.
            db.Entry(record).State = EntityState.Detached;
        }
    }

    /// <inheritdoc />
    // Batched like the inbox purge (same size and ceiling), keyed on the unique EntityId: short statements keep SQL
    // Server from escalating one large delete to a table lock that would block the create path's mapping inserts.
    public Task<int> PurgeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
        db.IdempotencyKeys.ExecuteDeleteBatchedAsync(
            e => e.CreatedUtc < cutoffUtc, e => e.EntityId, PurgeBatchSize, BatchedExecute.DefaultMaxBatches, ct);

    private Task<Guid?> FindAsync(Guid tenantId, string scope, string key, CancellationToken ct) =>
        db.IdempotencyKeys.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Scope == scope && e.Key == key)
            .Select(e => (Guid?)e.EntityId)
            .FirstOrDefaultAsync(ct);
}
