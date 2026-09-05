using System.Diagnostics.Metrics;

namespace TaskFlow.Observability.Meters;

/// <summary>
/// Export streaming instruments. A streamed response has no meaningful request duration in the ASP.NET
/// metrics - it is one long request whose cost is the rows it produced - so row count and elapsed time are
/// recorded here, at the point the stream ends, including when the client disconnected early.
/// </summary>
public sealed class StreamingMeter
{
    public const string MeterName = "TaskFlow.Streaming";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _exportRows;
    private readonly Histogram<double> _exportDuration;

    /// <summary>Initializes streaming meter with required dependencies and default state.</summary>
    public StreamingMeter()
    {
        _exportRows = _meter.CreateCounter<long>(
            "taskflow.export.rows", "row", "Rows written to an export stream.");
        _exportDuration = _meter.CreateHistogram<double>(
            "taskflow.export.duration", "ms", "Time an export stream stayed open.");
    }

    /// <summary>Records one completed (or abandoned) export stream.</summary>
    public void RecordExport(long rows, double durationMs, bool completed)
    {
        var outcome = new KeyValuePair<string, object?>("export.outcome", completed ? "completed" : "abandoned");
        _exportRows.Add(rows, outcome);
        _exportDuration.Record(durationMs, outcome);
    }
}
