using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// The single AMQP connection for the process plus a bounded pool of publisher channels.
/// When an <see cref="IConnection"/> is already registered in the container (for example by
/// <c>Aspire.RabbitMQ.Client</c>) that connection is used and never disposed here; otherwise one is created from
/// <see cref="RabbitMqOptions.ConnectionString"/> and owned by this instance.
/// <see cref="RabbitMqOptions.PublisherChannelPoolSize"/> is read once at construction and is not hot-reloadable.
/// </summary>
internal sealed class RabbitMqConnectionMultiplexer : IRabbitMqConnectionMultiplexer
{
    private static readonly CreateChannelOptions PublisherChannelOptions =
        new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    private readonly IOptionsMonitor<RabbitMqOptions> _options;
    private readonly IServiceProvider _services;
    private readonly RabbitMqMetrics _metrics;
    private readonly ILogger<RabbitMqConnectionMultiplexer> _logger;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _poolGate;
    private readonly ConcurrentBag<IChannel> _idle = [];
    private readonly ConcurrentQueue<IChannel> _discarded = new();
    private IConnection? _connection;
    private bool _ownsConnection;
    private volatile bool _disposed;

    /// <summary>Creates the multiplexer.</summary>
    /// <param name="options">Transport options.</param>
    /// <param name="services">Container used to discover an already registered <see cref="IConnection"/>.</param>
    /// <param name="metrics">Instruments; the publisher pool gauge is fed from here.</param>
    /// <param name="logger">Logger.</param>
    public RabbitMqConnectionMultiplexer(
        IOptionsMonitor<RabbitMqOptions> options,
        IServiceProvider services,
        RabbitMqMetrics metrics,
        ILogger<RabbitMqConnectionMultiplexer> logger)
    {
        _options = options;
        _services = services;
        _metrics = metrics;
        _logger = logger;

        int poolSize = options.CurrentValue.PublisherChannelPoolSize;
        ArgumentOutOfRangeException.ThrowIfLessThan(poolSize, 1);
        _poolGate = new SemaphoreSlim(poolSize, poolSize);
    }

    /// <inheritdoc />
    public bool IsConnected => Volatile.Read(ref _connection)?.IsOpen == true;

    /// <inheritdoc />
    public async ValueTask<IConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IConnection? established = Volatile.Read(ref _connection);
        if (established is not null)
            return established;

        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
                return _connection;

            IConnection connection;
            if (_services.GetService<IConnection>() is { } registered)
            {
                _logger.ExternalConnectionUsed();
                connection = registered;
                _ownsConnection = false;
            }
            else
            {
                RabbitMqOptions options = _options.CurrentValue;
                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                    throw new InvalidOperationException($"No {nameof(IConnection)} is registered and {nameof(RabbitMqOptions)}.{nameof(RabbitMqOptions.ConnectionString)} is not set.");

                ConnectionFactory factory = new()
                {
                    Uri = new Uri(options.ConnectionString),
                    ClientProvidedName = options.ClientProvidedName,
                    AutomaticRecoveryEnabled = options.AutomaticRecoveryEnabled,
                    TopologyRecoveryEnabled = true,
                    NetworkRecoveryInterval = options.NetworkRecoveryInterval
                };

                connection = await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
                _ownsConnection = true;
                _logger.ConnectionEstablished(options.ClientProvidedName, connection.Endpoint.ToString());
            }

            connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            Volatile.Write(ref _connection, connection);
            return connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<PooledChannel> RentPublisherChannelAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _poolGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DrainDiscardedAsync().ConfigureAwait(false);

            while (_idle.TryTake(out IChannel? pooled))
            {
                if (pooled.IsOpen)
                {
                    _metrics.PublisherPoolRentedAdd(1);
                    return new PooledChannel(pooled, this);
                }

                await pooled.DisposeAsync().ConfigureAwait(false);
            }

            IConnection connection = await GetConnectionAsync(ct).ConfigureAwait(false);
            IChannel channel = await connection.CreateChannelAsync(PublisherChannelOptions, ct).ConfigureAwait(false);
            _metrics.PublisherPoolRentedAdd(1);
            return new PooledChannel(channel, this);
        }
        catch
        {
            _poolGate.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IChannel> CreateConsumerChannelAsync(ushort prefetchCount, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(prefetchCount);

        IConnection connection = await GetConnectionAsync(ct).ConfigureAwait(false);
        IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: prefetchCount),
            ct).ConfigureAwait(false);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetchCount, global: false, ct).ConfigureAwait(false);
        return channel;
    }

    /// <summary>
    /// Returns a rented channel. An open channel goes back to the pool; a channel the broker closed is queued for
    /// disposal on the next rent so nothing is disposed on this synchronous path.
    /// </summary>
    internal void ReturnPublisherChannel(IChannel channel)
    {
        _metrics.PublisherPoolRentedAdd(-1);

        if (channel.IsOpen)
        {
            _idle.Add(channel);
        }
        else
        {
            _logger.PublisherChannelDiscarded(channel.ChannelNumber, channel.CloseReason?.ReplyText);
            _discarded.Enqueue(channel);
        }

        _poolGate.Release();
    }

    private async ValueTask DrainDiscardedAsync()
    {
        while (_discarded.TryDequeue(out IChannel? channel))
            await channel.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        _logger.ConnectionShutdown(args.ReplyCode, args.ReplyText);
        return Task.CompletedTask;
    }

    /// <summary>Disposes the pooled channels and, when this instance created it, the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await DrainDiscardedAsync().ConfigureAwait(false);
        while (_idle.TryTake(out IChannel? channel))
            await channel.DisposeAsync().ConfigureAwait(false);

        IConnection? connection = Volatile.Read(ref _connection);
        if (connection is not null)
        {
            connection.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
            if (_ownsConnection)
                await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connectionGate.Dispose();
        _poolGate.Dispose();
    }
}
