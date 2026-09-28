using EF.Common.Extensions;
using EF.Storage.Contracts;
using Microsoft.Extensions.Options;
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
    protected override Task HandleBatchAsync(
        IServiceProvider scope, IReadOnlyList<BlobDeleteWork> items, WorkBatchResult result, CancellationToken ct)
    {
        // IObjectStorageRepository always resolves - a no-op fallback stands in when no object-storage
        // backend is configured and treats a delete as already-gone (D-037), so no null guard is needed here.
        var blobs = scope.GetRequiredService<IObjectStorageRepository>();
        return DeleteBatchAsync(blobs, items, result, Options.MaxConcurrency, Logger, ct);
    }

    /// <summary>
    /// Deletes every blob in the batch with at most <paramref name="maxConcurrency"/> deletes in flight
    /// (D-055), and reports each row into <paramref name="result"/>. The rows are independent - each names one
    /// blob and nothing orders them - so the sequential loop this replaced spent the whole batch waiting on
    /// one storage round trip at a time. Nothing here touches the work repository: it is backed by the scoped
    /// DbContext, which is not thread safe, so the worker settles the outcomes sequentially afterwards.
    /// </summary>
    /// <param name="blobs">Blob store to delete from.</param>
    /// <param name="items">Claimed work rows.</param>
    /// <param name="result">Receives Complete for each deleted row and Fail for each row whose delete threw.</param>
    /// <param name="maxConcurrency">Deletes in flight; values below 1 are clamped to 1.</param>
    /// <param name="logger">Logger for failed deletes.</param>
    /// <param name="ct">Cancellation token; a requested cancellation propagates instead of being recorded.</param>
    public static async Task DeleteBatchAsync(
        IObjectStorageRepository blobs,
        IReadOnlyList<BlobDeleteWork> items,
        WorkBatchResult result,
        int maxConcurrency,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(result);

        await items.ToAsyncEnumerable().ConcurrentPipeAsync(async item =>
        {
            try
            {
                await blobs.DeleteAsync(item.ContainerName, item.BlobName, ct).ConfigureAwait(false);
                result.Complete(item.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a delete failure. The row stays unreported, so the worker abandons it without
                // consuming an attempt while the rows already deleted are still completed. Retrying a row whose
                // delete did land is safe, because a missing blob is a success.
                throw;
            }
            catch (Exception ex)
            {
                logger.BlobDeleteFailed(item.ContainerName, item.BlobName, ex);
                result.Fail(item.Id, ex);
            }
        }, Math.Max(1, maxConcurrency), ct).ConfigureAwait(false);
    }
}
