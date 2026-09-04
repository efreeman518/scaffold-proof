using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Drains deferred blob deletions staged by the domain delete path (D-026), so a domain delete never waits on
/// storage. A blob that is already gone counts as success: the work row describes an intent, not a precondition.
/// </summary>
public sealed class BlobDeleteWorkerService(
    IServiceScopeFactory scopeFactory,
    ILogger<BlobDeleteWorkerService> logger)
    : LeasedWorkerBase<BlobDeleteWork>(scopeFactory, logger)
{
    /// <inheritdoc />
    protected override async Task HandleBatchAsync(
        IServiceProvider scope, IOperationalWorkRepository work, LeasedBatch<BlobDeleteWork> batch, CancellationToken ct)
    {
        var blobs = scope.GetService<IBlobStorageRepository>();
        if (blobs is null)
        {
            logger.BlobDeleteWorkerDisabled(batch.Items.Count);
            return;
        }

        var deleted = new List<Guid>(batch.Items.Count);
        foreach (var item in batch.Items)
        {
            try
            {
                await blobs.DeleteAsync(item.ContainerName, item.BlobName, ct).ConfigureAwait(false);
                deleted.Add(item.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.BlobDeleteFailed(item.ContainerName, item.BlobName, ex);
                await work.ReleaseAsync<BlobDeleteWork>(
                    batch.LeaseToken, item.Id, item.AttemptCount, ex.GetBaseException().Message, ct)
                    .ConfigureAwait(false);
            }
        }

        await work.CompleteAsync<BlobDeleteWork>(batch.LeaseToken, deleted, ct).ConfigureAwait(false);
    }
}
