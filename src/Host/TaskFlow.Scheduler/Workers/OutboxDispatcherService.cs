using Microsoft.Extensions.Options;
using System.Diagnostics;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Drains the transactional outbox (D-026): claim, group by destination, send, hard-delete. Settlement is per
/// message: a message the broker accepted is deleted even when its neighbours failed, a permanent failure (an
/// oversize message) is dead-lettered at once, and a transient one is released with a backoff, so one bad
/// message or channel does not stall - or re-send - the others. With no broker configured the loop never starts
/// and rows accumulate durably instead of being dropped.
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
        IServiceProvider scope, IReadOnlyList<OutboxMessage> items, WorkBatchResult result, CancellationToken ct)
    {
        metrics.RecordClaimBatch(items.Count);
        return DispatchBatchAsync(items, transport, result, metrics, Logger, ct);
    }

    /// <inheritdoc />
    protected override void OnDeadLettered(OutboxMessage item) => metrics.RecordDeadLettered(item.EventType);

    /// <summary>
    /// Sends each destination group and reports every message's outcome into <paramref name="result"/>. D-055:
    /// the sends run concurrently because the destinations are independent channels and a slow one used to hold
    /// up every other one behind it; the bound is the number of destinations, a handful by construction (one
    /// logical channel per event family), so <c>Task.WhenAll</c> is the whole limiter and a configured ceiling
    /// would be a knob with no range.
    ///
    /// No Channel pipeline here, deliberately (D-055): this is a lease-based poller, so the claimed batch
    /// already bounds work in flight and the lease is what makes a crash recoverable. A channel would add a
    /// second in-memory buffer holding rows that are leased but not yet sent - more state to lose on a crash,
    /// no extra throughput, because the limit is the broker round trip, not the loop.
    ///
    /// Nothing here touches the work repository: it is backed by the scoped DbContext, which is not thread safe,
    /// so the worker settles the reported outcomes sequentially afterwards. A group whose send is abandoned by
    /// shutdown reports nothing and is abandoned; every group that finished is still settled.
    /// </summary>
    /// <param name="items">Claimed rows.</param>
    /// <param name="transport">Broker transport.</param>
    /// <param name="result">Receives one outcome per sent or failed message.</param>
    /// <param name="metrics">Messaging metrics.</param>
    /// <param name="logger">Logger for dispatch failures.</param>
    /// <param name="ct">Stopping token.</param>
    public static async Task DispatchBatchAsync(
        IReadOnlyList<OutboxMessage> items,
        IIntegrationEventTransport transport,
        WorkBatchResult result,
        MessagingMetrics metrics,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(result);

        var groups = items
            .GroupBy(m => m.Destination, StringComparer.Ordinal)
            .Select(g => (Destination: g.Key, Messages: (IReadOnlyList<OutboxMessage>)[.. g]))
            .ToList();

        await Task.WhenAll(groups.Select(async group =>
        {
            var (destination, messages) = group;
            var started = Stopwatch.GetTimestamp();
            IReadOnlyList<OutboxSendFailure> failures;
            try
            {
                failures = await transport.SendBatchAsync(destination, messages, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown mid-send: report nothing, so the worker abandons exactly this group's rows while the
                // groups that did finish are still settled.
                return;
            }
            catch (Exception ex)
            {
                logger.OutboxDispatchFailed(destination, messages.Count, ex);
                foreach (var message in messages) result.Fail(message.Id, ex);
                metrics.RecordDispatchFailed(destination, messages.Count);
                return;
            }

            var failed = new HashSet<int>();
            foreach (var failure in failures)
            {
                if ((uint)failure.Index >= (uint)messages.Count || !failed.Add(failure.Index))
                    throw new InvalidOperationException(
                        $"Transport reported failure index {failure.Index} for a send of {messages.Count} to {destination}.");
                result.Fail(messages[failure.Index].Id, failure.Error, failure.Permanent);
            }

            for (var i = 0; i < messages.Count; i++)
            {
                if (!failed.Contains(i)) result.Complete(messages[i].Id);
            }

            metrics.RecordDispatched(destination, messages.Count - failed.Count, Stopwatch.GetElapsedTime(started));
            metrics.RecordDispatchFailed(destination, failed.Count);
        })).ConfigureAwait(false);
    }
}
