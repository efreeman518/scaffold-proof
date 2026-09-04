using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Abstractions;

namespace TaskFlow.Scheduler.Handlers.Retention;

/// <summary>
/// Deletes dead-lettered outbox and blob-delete rows once they are past the retention window. Only
/// dead-lettered rows are eligible: a live row is still owed a dispatch however long it has been waiting, and
/// deleting it would drop the only surviving copy of the event.
/// </summary>
public sealed class OutboxRetentionHandler(
    IOperationalWorkRepository workRepository,
    SchedulerJobMeter meter,
    TimeProvider timeProvider,
    IConfiguration config) : IScheduledJobHandler
{
    public const string JobName = "OutboxRetention";
    private const int DefaultRetentionDays = 7;

    /// <summary>Handles outbox retention requests.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var cutoffUtc = timeProvider.GetUtcNow()
            .AddDays(-config.GetValue("Scheduling:Retention:OutboxDays", DefaultRetentionDays));

        meter.RecordRetention("outbox", await workRepository.PurgeDeadLetteredAsync<OutboxMessage>(cutoffUtc, ct));
        meter.RecordRetention("blobdelete", await workRepository.PurgeDeadLetteredAsync<BlobDeleteWork>(cutoffUtc, ct));
    }
}
