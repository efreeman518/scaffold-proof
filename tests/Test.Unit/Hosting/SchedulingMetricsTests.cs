using System.Diagnostics.Metrics;
using TaskFlow.Scheduler.Telemetry;

namespace Test.Unit.Hosting;

/// <summary>
/// The "failure.reason" tag must stay a small, bounded set (the exception's type name), not the
/// exception's free-form message: a message can carry an id, a path, or other high-cardinality detail
/// that would defeat the tag as a dimension. Pure-unit tier: a MeterListener observing the real
/// instrument, no DI container, host, or network.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SchedulingMetricsTests
{
    /// <summary>The recorded tag is the exception's type name, never its message text.</summary>
    [TestMethod]
    public void RecordJobFailure_TagsFailureReasonWithExceptionTypeName_NotMessage()
    {
        var metrics = new SchedulingMetrics();
        var tags = new List<KeyValuePair<string, object?>>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SchedulingMetrics.MeterName
                && instrument.Name == "scheduler.job.failures")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, measurementTags, _) =>
        {
            if (instrument.Name == "scheduler.job.failures")
                tags.AddRange(measurementTags.ToArray());
        });
        listener.Start();

        var exception = new InvalidOperationException("connection string contains a secret, not a tag value");
        metrics.RecordJobFailure("overdue-check", exception);

        var reason = tags.Single(t => t.Key == "failure.reason").Value;
        Assert.AreEqual(nameof(InvalidOperationException), reason);
        Assert.AreNotEqual(exception.Message, reason);
    }
}
