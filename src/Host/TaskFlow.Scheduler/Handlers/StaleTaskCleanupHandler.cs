using TaskFlow.Application.Contracts.Repositories;
using EF.BackgroundServices.Scheduling;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>
/// Removes cancelled tasks past their retention window. Per tenant batch, in one transaction: delete each task
/// under the stale guard (comments, checklist items, and tag links cascade), then, for the tasks that delete
/// removed, record the deferred blob deletions and delete the attachment rows. Blobs are never deleted inline -
/// a storage outage would otherwise block the row deletion.
/// </summary>
public sealed class StaleTaskCleanupHandler(
    ITaskItemSystemRepository systemRepository,
    ScheduledJobTelemetry telemetry,
    TimeProvider timeProvider,
    IConfiguration config,
    ILogger<StaleTaskCleanupHandler> logger) : IScheduledJobHandler
{
    public const string JobName = "StaleTaskCleanup";

    /// <summary>Tasks read per keyset page, and the size of one tenant transaction at most.</summary>
    private const int PageSize = 200;
    private const int DefaultRetentionDays = 90;

    /// <summary>Handles stale task cleanup requests and returns the application result.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var retentionDays = config.GetValue("Scheduling:StaleCleanup:RetentionDays", DefaultRetentionDays);
        var cutoffUtc = timeProvider.GetUtcNow().AddDays(-retentionDays);

        var scanned = 0;
        var deleted = 0;
        StaleTaskRow? after = null;

        while (true)
        {
            var batch = await systemRepository.GetStaleBatchAsync(cutoffUtc, after, PageSize, ct);
            if (batch.Count == 0) break;
            scanned += batch.Count;

            foreach (var tenant in batch.GroupBy(r => r.TenantId))
            {
                var ids = tenant.Select(r => r.Id).ToList();
                // The committed attempt's count only. A retry after a commit that landed finds no task left to
                // delete, so it stages no second blob-delete row.
                deleted += await systemRepository.ExecuteInTransactionAsync(async token =>
                {
                    // Blob work only for tasks this attempt's own guarded delete removed: a task another run
                    // removed first has its work rows already, under the same deterministic ids.
                    var removed = new List<Guid>(ids.Count);
                    foreach (var id in ids)
                    {
                        if (await systemRepository.DeleteStaleTaskAsync(tenant.Key, id, cutoffUtc, token)) removed.Add(id);
                    }

                    if (removed.Count == 0) return 0;
                    await systemRepository.StageBlobDeletesAsync(tenant.Key, removed, token);
                    await systemRepository.DeleteAttachmentsAsync(tenant.Key, removed, token);
                    return removed.Count;
                }, ct);
            }

            if (batch.Count < PageSize) break;
            // Resume past the last row scanned, not the last row deleted: a task skipped for still having
            // subtasks must not be re-read for the rest of this run.
            after = batch[^1];
        }

        telemetry.RecordWork(JobName, scanned, deleted);
        logger.StaleTasksFound(deleted, (int)(timeProvider.GetUtcNow() - cutoffUtc).TotalDays);
    }
}
