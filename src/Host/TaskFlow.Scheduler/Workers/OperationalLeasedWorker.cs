using EF.BackgroundServices.Leased;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Tracing;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Binds <see cref="LeasedWorkerBase{TOptions}"/> to one lease-claimable operational work table (D-026). The
/// package owns the drain loop, the adaptive backoff, the jitter and the lease token; what stays here is the
/// only part it cannot know: the scope the claim runs in, the repository that claims, and the D-053 span.
/// It runs on every replica - the lease, not a singleton election, is what stops two replicas doing the same row.
/// </summary>
/// <typeparam name="TWork">Work row type drained by this worker.</typeparam>
/// <typeparam name="TOptions">Options controlling batch size, lease duration and poll backoff.</typeparam>
public abstract class OperationalLeasedWorker<TWork, TOptions>(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<TOptions> options,
    ILoggerFactory loggerFactory)
    : LeasedWorkerBase<TOptions>(options, loggerFactory)
    where TWork : OperationalWorkBase
    where TOptions : LeasedWorkerOptions
{
    /// <summary>Handles one claimed batch. Implementations own completion and release of every row.</summary>
    protected abstract Task HandleBatchAsync(
        IServiceProvider scope, IOperationalWorkRepository work, LeasedBatch<TWork> batch, CancellationToken ct);

    /// <inheritdoc />
    /// <remarks>
    /// The package's <paramref name="leaseToken"/> is not used: the claim is a conditional UPDATE that mints
    /// its own token per batch (<see cref="LeasedBatch{TWork}.LeaseToken"/>), and the row's owner column is
    /// the replica identity so a stuck lease is diagnosable from the row alone.
    /// </remarks>
    protected override async Task<int> ProcessBatchAsync(string leaseToken, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<IOperationalWorkRepository>();

        var options = Options;
        var batch = await work
            .ClaimAsync<TWork>(options.BatchSize, options.LeaseDuration, LeaseOwner, ct)
            .ConfigureAwait(false);
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
