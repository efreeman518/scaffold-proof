using System.Collections.Concurrent;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>
/// Singleton record of what the consumer handed to the handler, plus the behaviour a test wants the handler to
/// show. Tracks concurrent invocations so a test can assert the prefetch bound without the management API's delay.
/// </summary>
internal sealed class HandlerScript
{
    private readonly ConcurrentQueue<RabbitMqDelivery> _deliveries = new();
    private int _inFlight;
    private int _peakInFlight;

    /// <summary>What the handler returns; the default acknowledges everything.</summary>
    internal Func<RabbitMqDelivery, CancellationToken, Task<ConsumeResult>> Behavior { get; set; } =
        (_, _) => Task.FromResult(ConsumeResult.Ack);

    internal IReadOnlyCollection<RabbitMqDelivery> Deliveries => _deliveries;

    internal int Count => _deliveries.Count;

    internal int InFlight => Volatile.Read(ref _inFlight);

    internal int PeakInFlight => Volatile.Read(ref _peakInFlight);

    internal async Task<ConsumeResult> RunAsync(RabbitMqDelivery delivery, CancellationToken ct)
    {
        _deliveries.Enqueue(delivery);
        int current = Interlocked.Increment(ref _inFlight);
        RecordPeak(current);
        try
        {
            return await Behavior(delivery, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void RecordPeak(int current)
    {
        int observed = Volatile.Read(ref _peakInFlight);
        while (current > observed)
        {
            int previous = Interlocked.CompareExchange(ref _peakInFlight, current, observed);
            if (previous == observed)
                return;

            observed = previous;
        }
    }
}

/// <summary>Scoped handler that defers to the test's <see cref="HandlerScript"/>.</summary>
internal sealed class ScriptedHandler(HandlerScript script) : IRabbitMqMessageHandler
{
    public Task<ConsumeResult> HandleAsync(RabbitMqDelivery delivery, CancellationToken ct) => script.RunAsync(delivery, ct);
}

/// <summary>Scoped dependency whose identity reveals whether each delivery got its own scope.</summary>
internal sealed class ScopeMarker
{
    internal Guid Id { get; } = Guid.NewGuid();
}

/// <summary>Singleton log of the scope identities the handler saw.</summary>
internal sealed class ScopeLog
{
    internal ConcurrentBag<Guid> Ids { get; } = [];
}

/// <summary>Scoped handler that records the identity of its scoped dependency and acknowledges.</summary>
internal sealed class ScopeCapturingHandler(ScopeMarker marker, ScopeLog log) : IRabbitMqMessageHandler
{
    public Task<ConsumeResult> HandleAsync(RabbitMqDelivery delivery, CancellationToken ct)
    {
        log.Ids.Add(marker.Id);
        return Task.FromResult(ConsumeResult.Ack);
    }
}
