using RabbitMQ.Client;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// Owns the single AMQP connection for the process and hands out channels: pooled publisher channels with
/// confirms enabled, and dedicated consumer channels with QoS applied.
/// </summary>
public interface IRabbitMqConnectionMultiplexer : IAsyncDisposable
{
    /// <summary>True when a connection has been established and is open.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Returns the process connection, creating it on first use. Creation is serialized, so concurrent callers
    /// share one connection.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IConnection> GetConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Rents a publisher channel with publisher confirms and confirm tracking enabled. Waits asynchronously while
    /// the pool is exhausted. Dispose the returned <see cref="PooledChannel"/> to release it.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PooledChannel> RentPublisherChannelAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a dedicated consumer channel with <c>BasicQos(0, prefetchCount, global: false)</c> applied and a
    /// dispatch concurrency equal to the prefetch count. Not pooled; the caller owns and disposes it.
    /// </summary>
    /// <param name="prefetchCount">Unacknowledged deliveries the broker may have outstanding on the channel.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IChannel> CreateConsumerChannelAsync(ushort prefetchCount, CancellationToken ct = default);
}

/// <summary>
/// A publisher channel borrowed from the multiplexer's pool. Disposing returns the channel to the pool; a channel
/// the broker has closed is discarded and disposed instead of being reused.
/// </summary>
public readonly struct PooledChannel : IDisposable, IEquatable<PooledChannel>
{
    private readonly RabbitMqConnectionMultiplexer? _owner;

    internal PooledChannel(IChannel channel, RabbitMqConnectionMultiplexer owner)
    {
        Channel = channel;
        _owner = owner;
    }

    /// <summary>The borrowed channel. Valid until this instance is disposed.</summary>
    public IChannel Channel { get; }

    /// <summary>Releases the channel back to the pool, or discards it when the broker has closed it.</summary>
    public void Dispose() => _owner?.ReturnPublisherChannel(Channel);

    /// <inheritdoc />
    public bool Equals(PooledChannel other) => ReferenceEquals(Channel, other.Channel) && ReferenceEquals(_owner, other._owner);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PooledChannel other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Channel?.GetHashCode() ?? 0;

    /// <summary>Compares two borrowed channels by identity.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator ==(PooledChannel left, PooledChannel right) => left.Equals(right);

    /// <summary>Compares two borrowed channels by identity.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator !=(PooledChannel left, PooledChannel right) => !left.Equals(right);
}
