using System.Diagnostics;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Tracing;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Adaptive-poll drain loop over one lease-claimable work table (D-026). It runs on every replica: the lease,
/// not a singleton election, is what stops two replicas doing the same row.
/// fallback: replace with EF.BackgroundServices.LeasedWorkerBase when published (package request 11).
/// </summary>
/// <typeparam name="TWork">Work row type drained by this worker.</typeparam>
public abstract class LeasedWorkerBase<TWork>(
    IServiceScopeFactory scopeFactory,
    ILogger logger,
    TimeProvider? timeProvider = null) : BackgroundService
    where TWork : OperationalWorkBase
{
    /// <summary>Fastest poll, also the delay after a batch that had work.</summary>
    protected static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Slowest poll, reached by doubling while the table stays empty.</summary>
    protected static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Rows claimed per poll.</summary>
    protected virtual int BatchSize => 50;

    /// <summary>How long a claim is owned before another replica may steal it.</summary>
    protected virtual TimeSpan LeaseDuration => TimeSpan.FromMinutes(5);

    /// <summary>Replica identity written to LeaseOwner; unique per process, diagnosable from the row.</summary>
    protected static string LeaseOwner { get; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Handles one claimed batch. Implementations own completion and release of every row.</summary>
    protected abstract Task HandleBatchAsync(
        IServiceProvider scope, IOperationalWorkRepository work, LeasedBatch<TWork> batch, CancellationToken ct);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = MinPollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;
            try
            {
                claimed = await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A claim-side failure (database unavailable) must not kill the host; back off and retry.
                logger.LeasedWorkerPollFailed(typeof(TWork).Name, ex);
                claimed = 0;
            }

            delay = NextDelay(delay, claimed, BatchSize);
            if (delay == TimeSpan.Zero) continue;

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Zero when the batch came back full (drain as fast as the table allows), the floor after partial work,
    /// and a doubling up to the ceiling while the table is empty.
    /// </summary>
    public static TimeSpan NextDelay(TimeSpan current, int claimed, int batchSize)
    {
        if (claimed >= batchSize) return TimeSpan.Zero;
        if (claimed > 0) return MinPollInterval;

        var next = current <= TimeSpan.Zero ? MinPollInterval : current * 2;
        return next > MaxPollInterval ? MaxPollInterval : next;
    }

    private async Task<int> PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<IOperationalWorkRepository>();

        var batch = await work.ClaimAsync<TWork>(BatchSize, LeaseDuration, LeaseOwner, ct).ConfigureAwait(false);
        if (batch.Items.Count == 0) return 0;

        // D-053: one span per non-empty drain, started after the claim so an idle poll emits nothing. Empty
        // polls are the common case here (the loop backs off to a 5s floor), and a span for each of them
        // would bury the drains that did work.
        using var activity = TaskFlowActivitySources.Scheduler.StartActivity(
            $"{typeof(TWork).Name} drain", ActivityKind.Internal);
        activity?.SetTag("scheduler.work.type", typeof(TWork).Name)
            .SetTag("scheduler.work.claimed", batch.Items.Count);

        await HandleBatchAsync(scope.ServiceProvider, work, batch, ct).ConfigureAwait(false);
        return batch.Items.Count;
    }
}
