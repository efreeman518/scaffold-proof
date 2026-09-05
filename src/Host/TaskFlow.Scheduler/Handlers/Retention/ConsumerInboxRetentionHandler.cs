using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Abstractions;

namespace TaskFlow.Scheduler.Handlers.Retention;

/// <summary>
/// Trims the consumer inbox (D-029). The window must stay longer than any broker's maximum redelivery age -
/// a claim deleted too early lets a late redelivery through and the consumer runs its effect twice.
/// </summary>
public sealed class ConsumerInboxRetentionHandler(
    IInboxStore inboxStore,
    SchedulerJobMeter meter,
    TimeProvider timeProvider,
    IConfiguration config) : IScheduledJobHandler
{
    public const string JobName = "ConsumerInboxRetention";
    private const int DefaultRetentionDays = 7;

    /// <summary>Handles consumer inbox retention requests.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var cutoffUtc = timeProvider.GetUtcNow()
            .AddDays(-config.GetValue("Scheduling:Retention:ConsumerInboxDays", DefaultRetentionDays));

        meter.RecordRetention("consumerinbox", await inboxStore.PurgeProcessedAsync(cutoffUtc, ct));
    }
}
