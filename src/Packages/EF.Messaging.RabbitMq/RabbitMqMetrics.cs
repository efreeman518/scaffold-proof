using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// OpenTelemetry instruments for the transport, published on meter <c>EF.Messaging.RabbitMq</c>.
/// Counters: <c>ef.rabbitmq.published</c>, <c>ef.rabbitmq.publish.nacked</c>, <c>ef.rabbitmq.consumed</c>,
/// <c>ef.rabbitmq.deadlettered</c>. Histograms: <c>ef.rabbitmq.publish.confirm.duration</c>,
/// <c>ef.rabbitmq.consume.duration</c>. Observable gauges: <c>ef.rabbitmq.consumer.inflight</c>,
/// <c>ef.rabbitmq.publisher.pool.rented</c>.
/// </summary>
public sealed class RabbitMqMetrics : IDisposable
{
    /// <summary>Meter name to add to an OpenTelemetry metrics pipeline.</summary>
    public const string MeterName = "EF.Messaging.RabbitMq";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _published;
    private readonly Counter<long> _publishNacked;
    private readonly Counter<long> _consumed;
    private readonly Counter<long> _deadLettered;
    private readonly Histogram<double> _confirmDuration;
    private readonly Histogram<double> _consumeDuration;
    private readonly ConcurrentDictionary<string, int> _inFlight = new();
    private int _poolRented;

    /// <summary>Creates the meter and its instruments.</summary>
    public RabbitMqMetrics()
    {
        _published = _meter.CreateCounter<long>("ef.rabbitmq.published", "{message}", "Messages accepted for publishing.");
        _publishNacked = _meter.CreateCounter<long>("ef.rabbitmq.publish.nacked", "{message}", "Messages the broker did not confirm.");
        _consumed = _meter.CreateCounter<long>("ef.rabbitmq.consumed", "{message}", "Deliveries handled, by outcome.");
        _deadLettered = _meter.CreateCounter<long>("ef.rabbitmq.deadlettered", "{message}", "Deliveries rejected to a dead-letter exchange.");
        _confirmDuration = _meter.CreateHistogram<double>("ef.rabbitmq.publish.confirm.duration", "ms", "Time to confirm a publish batch.");
        _consumeDuration = _meter.CreateHistogram<double>("ef.rabbitmq.consume.duration", "ms", "Handler duration per delivery.");
        _meter.CreateObservableGauge("ef.rabbitmq.consumer.inflight", ObserveInFlight, "{delivery}", "Deliveries currently being handled.");
        _meter.CreateObservableGauge("ef.rabbitmq.publisher.pool.rented", () => Volatile.Read(ref _poolRented), "{channel}", "Publisher channels currently rented.");
    }

    private IEnumerable<Measurement<int>> ObserveInFlight()
    {
        foreach (KeyValuePair<string, int> entry in _inFlight)
            yield return new Measurement<int>(entry.Value, new KeyValuePair<string, object?>("queue", entry.Key));
    }

    internal void Published(string exchange, int count) =>
        _published.Add(count, new KeyValuePair<string, object?>("exchange", exchange));

    internal void PublishNacked(string exchange, int count) =>
        _publishNacked.Add(count, new KeyValuePair<string, object?>("exchange", exchange));

    internal void PublishConfirmDuration(string exchange, double milliseconds) =>
        _confirmDuration.Record(milliseconds, new KeyValuePair<string, object?>("exchange", exchange));

    internal void Consumed(string queue, ConsumeOutcome outcome) =>
        _consumed.Add(1, new KeyValuePair<string, object?>("queue", queue), new KeyValuePair<string, object?>("outcome", outcome.ToString()));

    internal void DeadLettered(string queue) =>
        _deadLettered.Add(1, new KeyValuePair<string, object?>("queue", queue));

    internal void ConsumeDuration(string queue, double milliseconds) =>
        _consumeDuration.Record(milliseconds, new KeyValuePair<string, object?>("queue", queue));

    internal void ConsumerInFlightAdd(string queue, int delta) =>
        _inFlight.AddOrUpdate(queue, delta, (_, current) => current + delta);

    internal int ConsumerInFlight(string queue) => _inFlight.TryGetValue(queue, out int value) ? value : 0;

    internal void PublisherPoolRentedAdd(int delta) => Interlocked.Add(ref _poolRented, delta);

    internal int PublisherPoolRented => Volatile.Read(ref _poolRented);

    /// <summary>Disposes the meter.</summary>
    public void Dispose() => _meter.Dispose();
}
