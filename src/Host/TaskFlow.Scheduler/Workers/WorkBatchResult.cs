using System.Collections.Concurrent;

namespace TaskFlow.Scheduler.Workers;

/// <summary>
/// Per-row outcomes a work-table handler reports for one claimed batch; <see cref="OperationalLeasedWorker{TWork,TOptions}"/>
/// settles them after the handler returns. Thread safe, because handlers run rows concurrently (D-055), and the
/// first report for a row wins, so a late duplicate report cannot flip a completed row into a retry.
/// </summary>
public sealed class WorkBatchResult
{
    private readonly HashSet<Guid> _batch;
    private readonly ConcurrentDictionary<Guid, WorkItemOutcome> _outcomes = new();

    /// <summary>Creates the result for the ids of one claimed batch.</summary>
    /// <param name="ids">Ids of the claimed rows.</param>
    public WorkBatchResult(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _batch = [.. ids];
    }

    /// <summary>Reports a row handled successfully; it is hard-deleted.</summary>
    public void Complete(Guid id) => Report(id, WorkItemOutcome.Completed);

    /// <summary>Reports a failed row with the exception that stopped it.</summary>
    /// <param name="id">Row id.</param>
    /// <param name="error">Failure; its base exception's type and message are recorded on the row.</param>
    /// <param name="permanent">True when retrying cannot succeed; the row is dead-lettered at once.</param>
    public void Fail(Guid id, Exception error, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        var root = error.GetBaseException();
        Fail(id, $"{root.GetType().Name}: {root.Message}", permanent);
    }

    /// <summary>Reports a failed row with a reason.</summary>
    /// <param name="id">Row id.</param>
    /// <param name="error">Reason recorded on the row.</param>
    /// <param name="permanent">True when retrying cannot succeed; the row is dead-lettered at once.</param>
    public void Fail(Guid id, string error, bool permanent = false) =>
        Report(id, WorkItemOutcome.Failed(error ?? string.Empty, permanent));

    /// <summary>The outcome reported for <paramref name="id"/>, or null when none was.</summary>
    public WorkItemOutcome? OutcomeOf(Guid id) => _outcomes.TryGetValue(id, out var outcome) ? outcome : null;

    private void Report(Guid id, WorkItemOutcome outcome)
    {
        if (!_batch.Contains(id))
            throw new ArgumentException($"Work item {id} is not part of this batch.", nameof(id));

        _outcomes.TryAdd(id, outcome);
    }
}
