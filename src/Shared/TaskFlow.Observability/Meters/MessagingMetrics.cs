using System.Diagnostics.Metrics;

namespace TaskFlow.Observability.Meters;

/// <summary>
/// Instruments for the durable messaging path (outbox, transports, consumers). Registered as a singleton and
/// added to the OpenTelemetry metrics pipeline by name through <see cref="MeterName"/>. RabbitMQ publish confirms
/// are counted by the package's own <c>EF.Messaging.RabbitMq</c> meter (<c>ef.rabbitmq.published</c>,
/// <c>ef.rabbitmq.publish.nacked</c>), not here.
/// </summary>
public sealed class MessagingMetrics : IDisposable
{
    /// <summary>Meter name to register with the OpenTelemetry metrics pipeline.</summary>
    public const string MeterName = "TaskFlow.Messaging";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _staged;
    private readonly Counter<long> _dispatched;
    private readonly Counter<long> _deadLettered;
    private readonly Histogram<int> _claimBatchSize;
    private readonly Histogram<double> _dispatchDuration;
    private readonly Counter<long> _dispatchFailed;
    private readonly Counter<long> _inboxDuplicate;
    private readonly Counter<long> _inboxInProgress;
    private readonly Histogram<double> _consumerDuration;

    // Backlog is a level, not an event: gauges read the last snapshot the health check took, so the meter
    // never opens its own database connection on a scrape.
    private int _outboxPending;
    private double _outboxLagSeconds;
    private int _blobDeletePending;

    /// <summary>Creates the instruments. Registered as a singleton and exported by meter name.</summary>
    public MessagingMetrics()
    {
        _staged = _meter.CreateCounter<long>("taskflow.outbox.staged", "{message}", "Outbox rows staged by the persistence interceptor.");
        _dispatched = _meter.CreateCounter<long>("taskflow.outbox.dispatched", "{message}", "Outbox rows handed to a transport and deleted.");
        _dispatchFailed = _meter.CreateCounter<long>("taskflow.outbox.dispatch.failed", "{message}", "Outbox rows a transport did not accept; released or dead-lettered.");
        _deadLettered = _meter.CreateCounter<long>("taskflow.outbox.deadlettered", "{message}", "Outbox rows parked after a permanent failure or the attempt ceiling.");
        _claimBatchSize = _meter.CreateHistogram<int>("taskflow.outbox.claim.batch_size", "{row}", "Rows returned by one lease claim.");
        _dispatchDuration = _meter.CreateHistogram<double>("taskflow.outbox.dispatch.duration", "ms", "Time to send one destination batch.");
        _inboxDuplicate = _meter.CreateCounter<long>("taskflow.inbox.duplicate", "{message}", "Deliveries rejected by the consumer inbox.");
        _inboxInProgress = _meter.CreateCounter<long>("taskflow.inbox.in_progress", "{message}", "Deliveries sent back for retry because another delivery holds a live claim.");
        _consumerDuration = _meter.CreateHistogram<double>("taskflow.consumer.duration", "ms", "Time to handle one consumed message.");

        _meter.CreateObservableGauge("taskflow.outbox.pending", () => Volatile.Read(ref _outboxPending), "{row}", "Live outbox rows awaiting dispatch.");
        _meter.CreateObservableGauge("taskflow.outbox.lag", () => Volatile.Read(ref _outboxLagSeconds), "s", "Age of the oldest due outbox row.");
        _meter.CreateObservableGauge("taskflow.blobdelete.pending", () => Volatile.Read(ref _blobDeletePending), "{row}", "Blob-delete work rows awaiting a worker.");
    }

    /// <summary>Publishes the latest backlog snapshot to the gauges. Called by the outbox health check.</summary>
    public void RecordBacklog(int outboxPending, TimeSpan outboxLag, int blobDeletePending)
    {
        Volatile.Write(ref _outboxPending, outboxPending);
        Volatile.Write(ref _outboxLagSeconds, outboxLag.TotalSeconds);
        Volatile.Write(ref _blobDeletePending, blobDeletePending);
    }

    /// <summary>Records rows staged in one SaveChanges.</summary>
    public void RecordStaged(int count) => _staged.Add(count);

    /// <summary>Records the size of one lease claim.</summary>
    public void RecordClaimBatch(int size) => _claimBatchSize.Record(size);

    /// <summary>Records a destination batch that reached the broker.</summary>
    public void RecordDispatched(string destination, int count, TimeSpan elapsed)
    {
        var tag = new KeyValuePair<string, object?>("destination", destination);
        _dispatched.Add(count, tag);
        _dispatchDuration.Record(elapsed.TotalMilliseconds, tag);
    }

    /// <summary>Records outbox rows a transport did not accept.</summary>
    public void RecordDispatchFailed(string destination, int count)
    {
        if (count > 0) _dispatchFailed.Add(count, new KeyValuePair<string, object?>("destination", destination));
    }

    /// <summary>Records one row the store actually parked (the dead-letter statement changed a row).</summary>
    public void RecordDeadLettered(string eventType) => _deadLettered.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    /// <summary>Records a delivery the consumer inbox already had.</summary>
    public void RecordInboxDuplicate(string consumer) => _inboxDuplicate.Add(1, new KeyValuePair<string, object?>("consumer", consumer));

    /// <summary>Records a delivery sent back because another delivery holds a live claim on the same message.</summary>
    public void RecordInboxInProgress(string consumer) => _inboxInProgress.Add(1, new KeyValuePair<string, object?>("consumer", consumer));

    /// <summary>Records the time one consumer spent on a delivery.</summary>
    public void RecordConsumerDuration(string consumer, TimeSpan elapsed)
        => _consumerDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("consumer", consumer));

    /// <summary>Disposes the meter.</summary>
    public void Dispose() => _meter.Dispose();
}
