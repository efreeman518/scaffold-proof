using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Abstractions;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>
/// Removes cancelled tasks past their retention window. Per tenant batch, in one transaction: record the
/// deferred blob deletions, delete the attachments, then delete the tasks (comments, checklist items, and tag
/// links cascade). Blobs are never deleted inline - a storage outage would otherwise block the row deletion.
/// </summary>
public sealed class StaleTaskCleanupHandler(
    ITaskItemSystemRepository systemRepository,
    SchedulerJobMeter meter,
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
                await systemRepository.ExecuteInTransactionAsync(async token =>
                {
                    await systemRepository.StageBlobDeletesAsync(tenant.Key, ids, token);
                    deleted += await systemRepository.DeleteStaleBatchAsync(tenant.Key, ids, cutoffUtc, token);
                }, ct);
            }

            if (batch.Count < PageSize) break;
            // Resume past the last row scanned, not the last row deleted: a task skipped for still having
            // subtasks must not be re-read for the rest of this run.
            after = batch[^1];
        }

        meter.RecordWork(JobName, scanned, deleted);
        logger.StaleTasksFound(deleted, (int)(timeProvider.GetUtcNow() - cutoffUtc).TotalDays);
    }
}
