using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace TaskFlow.Infrastructure.Repositories;

// fallback: replace with EF.Data.Contracts ExecuteDeleteBatchedAsync when published (package request 10).
/// <summary>
/// Bounded <c>ExecuteDelete</c> loop for retention sweeps. A single unbounded delete over a retention
/// window can lock a whole table and blow the log; this walks it in fixed batches and stops after
/// <c>maxBatches</c> so one run of a cron job has a hard ceiling on how long it can hold the database.
/// Whatever is left is picked up by the next run.
/// </summary>
public static class BatchedExecute
{
    public const int DefaultBatchSize = 1000;
    public const int DefaultMaxBatches = 100;

    /// <summary>
    /// Runs <paramref name="executeBatch"/> until it reports a short batch or <paramref name="maxBatches"/>
    /// is reached, returning the total row count. Split out from the EF call so the loop's stopping rule is
    /// testable without a database.
    /// </summary>
    public static async Task<int> RunBatchedAsync(
        Func<int, CancellationToken, Task<int>> executeBatch,
        int batchSize = DefaultBatchSize,
        int maxBatches = DefaultMaxBatches,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatches, 1);

        var total = 0;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            var affected = await executeBatch(batchSize, ct).ConfigureAwait(false);
            total += affected;
            if (affected < batchSize) break;
        }

        return total;
    }

    /// <summary>
    /// Deletes every row matching <paramref name="source"/> in batches keyed on <paramref name="keySelector"/>.
    /// The keys for one batch are read first and the delete is restricted to them: <c>Take</c> is not part of
    /// the <c>ExecuteDelete</c> query itself, which no relational provider is required to translate.
    /// </summary>
    public static Task<int> ExecuteDeleteBatchedAsync<TEntity, TKey>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, TKey>> keySelector,
        int batchSize = DefaultBatchSize,
        int maxBatches = DefaultMaxBatches,
        CancellationToken ct = default)
        where TEntity : class =>
        RunBatchedAsync(async (size, token) =>
        {
            var keys = await source.OrderBy(keySelector).Select(keySelector).Take(size)
                .ToListAsync(token).ConfigureAwait(false);
            if (keys.Count == 0) return 0;

            // A concurrent deleter can make this report fewer rows than keys read; the loop then stops one
            // batch early and the next run finishes the window. Never fewer rows are deleted, only later.
            return await source.Where(KeyIn(keySelector, keys)).ExecuteDeleteAsync(token).ConfigureAwait(false);
        }, batchSize, maxBatches, ct);

    /// <summary>Builds <c>e =&gt; keys.Contains(keySelector(e))</c> so EF emits a single IN predicate.</summary>
    private static Expression<Func<TEntity, bool>> KeyIn<TEntity, TKey>(
        Expression<Func<TEntity, TKey>> keySelector, List<TKey> keys)
    {
        var contains = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            [typeof(TKey)],
            Expression.Constant(keys),
            keySelector.Body);
        return Expression.Lambda<Func<TEntity, bool>>(contains, keySelector.Parameters[0]);
    }
}
