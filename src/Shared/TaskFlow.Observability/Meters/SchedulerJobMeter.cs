using System.Diagnostics.Metrics;

namespace TaskFlow.Observability.Meters;

/// <summary>
/// Work-volume instruments for the scheduler jobs, published on the same <c>TaskFlow.Scheduler</c> meter as
/// the host's execution counters (one OpenTelemetry <c>AddMeter</c> covers both). These answer the questions
/// the execution counters cannot: how much of the table a job had to read, and how much it actually changed.
/// A job whose scanned count climbs while affected stays flat has lost its index.
/// </summary>
public sealed class SchedulerJobMeter
{
    public const string MeterName = "TaskFlow.Scheduler";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _rowsScanned;
    private readonly Counter<long> _rowsAffected;
    private readonly Counter<long> _retentionRowsDeleted;

    /// <summary>Initializes scheduler job meter with required dependencies and default state.</summary>
    public SchedulerJobMeter()
    {
        _rowsScanned = _meter.CreateCounter<long>(
            "scheduler.job.rows_scanned", "row", "Rows a scheduler job read while looking for work.");
        _rowsAffected = _meter.CreateCounter<long>(
            "scheduler.job.rows_affected", "row", "Rows a scheduler job inserted, updated, or deleted.");
        _retentionRowsDeleted = _meter.CreateCounter<long>(
            "scheduler.retention.rows_deleted", "row", "Rows removed by a retention sweep, by store.");
    }

    /// <summary>Records one job run's scanned and affected row counts.</summary>
    public void RecordWork(string jobName, long rowsScanned, long rowsAffected)
    {
        var tag = new KeyValuePair<string, object?>("job.name", jobName);
        _rowsScanned.Add(rowsScanned, tag);
        _rowsAffected.Add(rowsAffected, tag);
    }

    /// <summary>Records rows deleted by a retention sweep of one store (outbox, inbox, tickerq, audit).</summary>
    public void RecordRetention(string store, long rowsDeleted) =>
        _retentionRowsDeleted.Add(rowsDeleted, new KeyValuePair<string, object?>("store", store));
}
