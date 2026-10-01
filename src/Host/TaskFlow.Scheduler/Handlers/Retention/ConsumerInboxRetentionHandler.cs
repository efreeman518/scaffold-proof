using EF.Messaging;
using EF.BackgroundServices.Scheduling;
using TaskFlow.Application.Contracts.Repositories;

namespace TaskFlow.Scheduler.Handlers.Retention;

/// <summary>
/// Trims the two deduplication records: the consumer inbox (D-029) and the Idempotency-Key mappings (D-074). Each
/// window must stay longer than its sender's retry horizon - a claim deleted too early lets a late redelivery
/// through, and a mapping deleted too early lets a late FlowEngine retry or lease recovery create a second row.
/// </summary>
public sealed class ConsumerInboxRetentionHandler(
    IInboxStore inboxStore,
    IIdempotencyKeyRepository idempotencyKeys,
    ScheduledJobTelemetry telemetry,
    TimeProvider timeProvider,
    IConfiguration config) : IScheduledJobHandler
{
    public const string JobName = "ConsumerInboxRetention";
    private const int DefaultRetentionDays = 7;

    /// <summary>Handles consumer inbox and idempotency-key retention requests.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        telemetry.RecordRetention("consumerinbox", await inboxStore.PurgeAsync(
            now.AddDays(-config.GetValue("Scheduling:Retention:ConsumerInboxDays", DefaultRetentionDays)), ct));
        telemetry.RecordRetention("idempotencykey", await idempotencyKeys.PurgeAsync(
            now.AddDays(-config.GetValue("Scheduling:Retention:IdempotencyKeyDays", DefaultRetentionDays)), ct));
    }
}
