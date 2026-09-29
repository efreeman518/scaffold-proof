using EF.BackgroundServices.Leased;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Tracing;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Binds <see cref="LeasedWorkerBase{TOptions}"/> to one lease-claimable operational work table (D-026). The
/// package owns the drain loop, the adaptive backoff and the jitter; what stays here is the part it cannot know:
/// the scope the claim runs in, the repository that claims, the D-053 span, and the settlement of every claimed
/// row. It runs on every replica - the lease, not a singleton election, is what stops two replicas doing the same row.
/// <para>
/// Handlers only report outcomes into a <see cref="WorkBatchResult"/>; settlement is centralized here so its
/// ordering is written once: it runs after the handler returns or throws, sequentially on the scoped repository
/// (the DbContext is not thread safe), and under its own bounded token that is deliberately NOT the stopping
/// token - a rolling deploy that stops the Scheduler right after the broker accepted a batch must still delete
/// those rows, or they are re-sent when the lease expires.
/// </para>
/// </summary>
/// <typeparam name="TWork">Work row type drained by this worker.</typeparam>
/// <typeparam name="TOptions">Options controlling batch size, lease duration, attempt ceiling and poll backoff.</typeparam>
public abstract class OperationalLeasedWorker<TWork, TOptions>(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<TOptions> options,
    ILoggerFactory loggerFactory)
    : LeasedWorkerBase<TOptions>(options, loggerFactory)
    where TWork : OperationalWorkBase
    where TOptions : LeasedWorkerOptions
{
    /// <summary>
    /// Budget for settling one batch. shortcut: a constant rather than an option; it only has to comfortably
    /// cover a handful of single-row statements, and must stay well below the lease duration.
    /// </summary>
    public static readonly TimeSpan SettlementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Handles one claimed batch and reports each row into <paramref name="result"/>. Must not touch the work
    /// repository. A row left unreported is abandoned when the host is stopping and retried otherwise.
    /// </summary>
    protected abstract Task HandleBatchAsync(
        IServiceProvider scope, IReadOnlyList<TWork> items, WorkBatchResult result, CancellationToken ct);

    /// <summary>Called once for each row the store actually dead-lettered.</summary>
    protected virtual void OnDeadLettered(TWork item)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// The claim is a conditional UPDATE that mints its own token per batch
    /// (<see cref="LeasedBatch{TWork}.LeaseToken"/>), and the row's owner column is the replica identity so a
    /// stuck lease is diagnosable from the row alone.
    /// </remarks>
    protected override async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<IOperationalWorkRepository>();

        var options = Options;
        var batch = await work
            .ClaimAsync<TWork>(options.BatchSize, options.LeaseDuration, options.MaxAttempts, LeaseOwner, ct)
            .ConfigureAwait(false);
        if (batch.Items.Count == 0) return 0;

        // D-053: one span per non-empty drain, started after the claim so an idle poll emits nothing. Empty
        // polls are the common case here (the loop backs off to a 5s floor), and a span for each of them
        // would bury the drains that did work.
        using var activity = TaskFlowActivitySources.Scheduler.StartActivity(
            $"{typeof(TWork).Name} drain", ActivityKind.Internal);
        activity?.SetTag("scheduler.work.type", typeof(TWork).Name)
            .SetTag("scheduler.work.claimed", batch.Items.Count);

        var result = new WorkBatchResult(batch.Items.Select(i => i.Id));
        Exception? handlerFailure = null;
        try
        {
            await HandleBatchAsync(scope.ServiceProvider, batch.Items, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: what was reported is settled below, what was not is abandoned, then the loop exits.
        }
        catch (Exception ex)
        {
            // Not rethrown: the rows it did not report are settled as transient failures below, which is what
            // gives them their backoff instead of leaving them leased until the lease expires.
            handlerFailure = ex;
            Logger.WorkBatchHandlerFailed(typeof(TWork).Name, batch.Items.Count, ex);
        }

        var stopping = ct.IsCancellationRequested;
        using (var settlement = new CancellationTokenSource(SettlementTimeout))
        {
            await SettleAsync(
                work, batch, result, options.MaxAttempts, stopping, handlerFailure, OnDeadLettered, Logger,
                settlement.Token).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return batch.Items.Count;
    }

    /// <summary>
    /// Settles one batch in a fixed order: one delete for every completed row; then each failure is dead-lettered
    /// when it is permanent or used its last attempt, and released with backoff otherwise; then rows nobody
    /// reported are abandoned (the host is stopping: no attempt consumed, no lease to wait out) or, when not
    /// stopping, released as transient failures. A statement that affects no row means the lease was lost to
    /// another replica after it expired; that is logged, never thrown. A store failure propagates - the rows stay
    /// leased and are reclaimed with the attempt counted, so delivery stays at-least-once.
    /// </summary>
    /// <param name="work">Repository backed by the batch's scoped DbContext.</param>
    /// <param name="batch">Claimed rows and their lease token.</param>
    /// <param name="result">Outcomes reported by the handler.</param>
    /// <param name="maxAttempts">Attempt ceiling from the worker options.</param>
    /// <param name="stopping">True when the host is shutting down.</param>
    /// <param name="handlerFailure">Exception the handler threw, recorded on the rows it did not report.</param>
    /// <param name="onDeadLettered">Called for each row the store actually dead-lettered.</param>
    /// <param name="logger">Logger for lost leases and dead-letters.</param>
    /// <param name="ct">Settlement token, independent of the stopping token.</param>
    public static async Task SettleAsync(
        IOperationalWorkRepository work,
        LeasedBatch<TWork> batch,
        WorkBatchResult result,
        int maxAttempts,
        bool stopping,
        Exception? handlerFailure,
        Action<TWork>? onDeadLettered,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(logger);

        var workType = typeof(TWork).Name;
        var completed = new List<Guid>();
        var failed = new List<(TWork Item, WorkItemOutcome Outcome)>();
        var unreported = new List<Guid>();
        var notSettled = handlerFailure is null
            ? "Not settled by the handler"
            : $"{handlerFailure.GetBaseException().GetType().Name}: {handlerFailure.GetBaseException().Message}";

        foreach (var item in batch.Items)
        {
            var outcome = result.OutcomeOf(item.Id);
            if (outcome is null)
            {
                if (stopping) unreported.Add(item.Id);
                else failed.Add((item, WorkItemOutcome.Failed(notSettled, permanent: false)));
            }
            else if (outcome.IsCompleted)
            {
                completed.Add(item.Id);
            }
            else
            {
                failed.Add((item, outcome));
            }
        }

        var leaseLost = 0;
        if (completed.Count > 0)
        {
            var deleted = await work.CompleteAsync<TWork>(batch.LeaseToken, completed, ct).ConfigureAwait(false);
            leaseLost += completed.Count - deleted;
        }

        foreach (var (item, outcome) in failed)
        {
            var error = outcome.Error ?? string.Empty;
            if (outcome.Permanent || item.AttemptCount >= maxAttempts)
            {
                if (await work.DeadLetterAsync<TWork>(batch.LeaseToken, item.Id, error, ct).ConfigureAwait(false))
                {
                    logger.WorkItemDeadLettered(workType, item.Id, item.AttemptCount, error);
                    onDeadLettered?.Invoke(item);
                }
                else
                {
                    leaseLost++;
                }
            }
            else if (!await work.ReleaseAsync<TWork>(batch.LeaseToken, item.Id, item.AttemptCount, error, ct)
                         .ConfigureAwait(false))
            {
                leaseLost++;
            }
        }

        if (unreported.Count > 0)
        {
            var abandoned = await work.AbandonAsync<TWork>(batch.LeaseToken, unreported, ct).ConfigureAwait(false);
            leaseLost += unreported.Count - abandoned;
        }

        if (leaseLost > 0) logger.WorkLeaseLost(workType, leaseLost, batch.LeaseToken);
    }
}
