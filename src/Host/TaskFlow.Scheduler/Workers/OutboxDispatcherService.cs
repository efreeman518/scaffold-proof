using System.Diagnostics;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Drains the transactional outbox (D-026): claim, group by destination, send, hard-delete. A destination that
/// fails releases only its own rows with a backoff, so one bad channel does not stall the others. With no broker
/// configured the loop never starts and rows accumulate durably instead of being dropped.
/// </summary>
public sealed class OutboxDispatcherService(
    IServiceScopeFactory scopeFactory,
    IIntegrationEventTransport transport,
    MessagingMetrics metrics,
    ILogger<OutboxDispatcherService> logger)
    : LeasedWorkerBase<OutboxMessage>(scopeFactory, logger)
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!transport.CanDispatch)
        {
            logger.OutboxDispatcherDisabled();
            return;
        }

        await base.ExecuteAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task HandleBatchAsync(
        IServiceProvider scope, IOperationalWorkRepository work, LeasedBatch<OutboxMessage> batch, CancellationToken ct)
    {
        metrics.RecordClaimBatch(batch.Items.Count);

        foreach (var group in batch.Items.GroupBy(m => m.Destination, StringComparer.Ordinal))
        {
            var messages = group.ToList();
            var started = Stopwatch.GetTimestamp();
            try
            {
                await transport.SendBatchAsync(group.Key, messages, ct).ConfigureAwait(false);
                await work.CompleteAsync<OutboxMessage>(batch.LeaseToken, [.. messages.Select(m => m.Id)], ct)
                    .ConfigureAwait(false);
                metrics.RecordDispatched(group.Key, messages.Count, Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.OutboxDispatchFailed(group.Key, messages.Count, ex);
                foreach (var message in messages)
                {
                    await work.ReleaseAsync<OutboxMessage>(
                        batch.LeaseToken, message.Id, message.AttemptCount, ex.GetBaseException().Message, ct)
                        .ConfigureAwait(false);
                    if (message.AttemptCount >= OperationalWorkBase.MaxAttempts)
                        metrics.RecordDeadLettered(message.EventType);
                }
            }
        }
    }
}
