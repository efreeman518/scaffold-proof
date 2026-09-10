using Microsoft.Extensions.Options;
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
    IOptionsMonitor<OutboxDispatcherSettings> options,
    ILoggerFactory loggerFactory)
    : OperationalLeasedWorker<OutboxMessage, OutboxDispatcherSettings>(scopeFactory, options, loggerFactory)
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!transport.CanDispatch)
        {
            Logger.OutboxDispatcherDisabled();
            return;
        }

        await base.ExecuteAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task HandleBatchAsync(
        IServiceProvider scope, IOperationalWorkRepository work, LeasedBatch<OutboxMessage> batch, CancellationToken ct)
    {
        metrics.RecordClaimBatch(batch.Items.Count);
        return DispatchBatchAsync(batch, transport, work, metrics, Logger, ct);
    }

    /// <summary>
    /// Sends each destination group and settles its rows. D-055: the sends run concurrently because the
    /// destinations are independent channels and a slow one used to hold up every other one behind it; the
    /// bound is the number of destinations, a handful by construction (one logical channel per event family),
    /// so <c>Task.WhenAll</c> is the whole limiter and a configured ceiling would be a knob with no range.
    ///
    /// No Channel pipeline here, deliberately (D-055): this is a lease-based poller, so the claimed batch
    /// already bounds work in flight and the lease is what makes a crash recoverable. A channel would add a
    /// second in-memory buffer holding rows that are leased but not yet sent - more state to lose on a crash,
    /// no extra throughput, because the limit is the broker round trip, not the loop.
    ///
    /// Settlement stays sequential and per-message, exactly as before: <paramref name="work"/> is backed by
    /// the scoped DbContext, which is not thread safe, so nothing touches it inside the concurrent phase.
    /// </summary>
    /// <param name="batch">Claimed rows with the lease token that guards their settlement.</param>
    /// <param name="transport">Broker transport.</param>
    /// <param name="work">Operational work repository owning complete/release.</param>
    /// <param name="metrics">Messaging metrics.</param>
    /// <param name="logger">Logger for dispatch failures.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task DispatchBatchAsync(
        LeasedBatch<OutboxMessage> batch,
        IIntegrationEventTransport transport,
        IOperationalWorkRepository work,
        MessagingMetrics metrics,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(work);

        var groups = batch.Items
            .GroupBy(m => m.Destination, StringComparer.Ordinal)
            .Select(g => (Destination: g.Key, Messages: (IReadOnlyList<OutboxMessage>)[.. g]))
            .ToList();

        var sends = await Task.WhenAll(groups.Select(async group =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await transport.SendBatchAsync(group.Destination, group.Messages, ct).ConfigureAwait(false);
                return (group, Elapsed: Stopwatch.GetElapsedTime(started), Error: (Exception?)null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown: abandon the whole batch with its lease intact rather than settling half of it.
                throw;
            }
            catch (Exception ex)
            {
                return (group, Elapsed: Stopwatch.GetElapsedTime(started), Error: (Exception?)ex);
            }
        })).ConfigureAwait(false);

        foreach (var ((destination, messages), elapsed, error) in sends)
        {
            if (error is null)
            {
                await work.CompleteAsync<OutboxMessage>(batch.LeaseToken, [.. messages.Select(m => m.Id)], ct)
                    .ConfigureAwait(false);
                metrics.RecordDispatched(destination, messages.Count, elapsed);
                continue;
            }

            logger.OutboxDispatchFailed(destination, messages.Count, error);
            foreach (var message in messages)
            {
                await work.ReleaseAsync<OutboxMessage>(
                    batch.LeaseToken, message.Id, message.AttemptCount, error.GetBaseException().Message, ct)
                    .ConfigureAwait(false);
                if (message.AttemptCount >= OperationalWorkBase.MaxAttempts)
                    metrics.RecordDeadLettered(message.EventType);
            }
        }
    }
}
