using EF.Common.Extensions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Drains deferred blob deletions staged by the domain delete path (D-026), so a domain delete never waits on
/// storage. A blob that is already gone counts as success: the work row describes an intent, not a precondition.
/// </summary>
public sealed class BlobDeleteWorkerService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<BlobDeleteSettings> options,
    ILoggerFactory loggerFactory)
    : OperationalLeasedWorker<BlobDeleteWork, BlobDeleteSettings>(scopeFactory, options, loggerFactory)
{
    /// <inheritdoc />
    protected override async Task HandleBatchAsync(
        IServiceProvider scope, IOperationalWorkRepository work, LeasedBatch<BlobDeleteWork> batch, CancellationToken ct)
    {
        // IBlobStorageRepository always resolves - a no-op fallback stands in when no object-storage
        // backend is configured and treats a delete as already-gone (D-037), so no null guard is needed here.
        var blobs = scope.GetRequiredService<IBlobStorageRepository>();

        var outcome = await DeleteBatchAsync(blobs, batch.Items, Options.MaxConcurrency, ct)
            .ConfigureAwait(false);

        // Lease bookkeeping stays sequential on purpose: IOperationalWorkRepository is backed by the scoped
        // DbContext, which is not thread safe, so it must not be touched from inside the concurrent phase.
        foreach (var (item, error) in outcome.Failed)
        {
            Logger.BlobDeleteFailed(item.ContainerName, item.BlobName, error);
            await work.ReleaseAsync<BlobDeleteWork>(
                batch.LeaseToken, item.Id, item.AttemptCount, error.GetBaseException().Message, ct)
                .ConfigureAwait(false);
        }

        await work.CompleteAsync<BlobDeleteWork>(batch.LeaseToken, outcome.Deleted, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every blob in the batch with at most <paramref name="maxConcurrency"/> deletes in flight
    /// (D-055), and reports which rows succeeded and which failed. The rows are independent - each names one
    /// blob and nothing orders them - so the sequential loop this replaced spent the whole batch waiting on
    /// one storage round trip at a time.
    /// </summary>
    /// <param name="blobs">Blob store to delete from.</param>
    /// <param name="items">Claimed work rows.</param>
    /// <param name="maxConcurrency">Deletes in flight; values below 1 are clamped to 1.</param>
    /// <param name="ct">Cancellation token; a requested cancellation propagates instead of being recorded.</param>
    /// <returns>Ids to complete, and the rows to release with the error that stopped them.</returns>
    public static async Task<(IReadOnlyCollection<Guid> Deleted, IReadOnlyCollection<(BlobDeleteWork Item, Exception Error)> Failed)>
        DeleteBatchAsync(
            IBlobStorageRepository blobs,
            IReadOnlyList<BlobDeleteWork> items,
            int maxConcurrency,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(items);

        var deleted = new ConcurrentQueue<Guid>();
        var failed = new ConcurrentQueue<(BlobDeleteWork, Exception)>();

        await items.ToAsyncEnumerable().ConcurrentPipeAsync(async item =>
        {
            try
            {
                await blobs.DeleteAsync(item.ContainerName, item.BlobName, ct).ConfigureAwait(false);
                deleted.Enqueue(item.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a delete failure. Rethrowing abandons the batch with its lease intact, so
                // the next replica to claim it retries every row - including the ones already deleted, which
                // is safe because a missing blob is a success.
                throw;
            }
            catch (Exception ex)
            {
                failed.Enqueue((item, ex));
            }
        }, Math.Max(1, maxConcurrency), ct).ConfigureAwait(false);

        return ([.. deleted], [.. failed]);
    }
}
