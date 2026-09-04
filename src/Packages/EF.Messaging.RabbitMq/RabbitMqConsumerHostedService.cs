using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// Consumes one queue on a dedicated channel with manual acknowledgement. Concurrency equals
/// <see cref="RabbitMqConsumerOptions.PrefetchCount"/>; every delivery runs <typeparamref name="THandler"/> inside a
/// fresh service scope. <see cref="ConsumeOutcome.Ack"/> acknowledges, <see cref="ConsumeOutcome.Retry"/> requeues
/// until the delivery bound is reached and then rejects so the broker routes to the queue's dead-letter exchange,
/// and <see cref="ConsumeOutcome.Reject"/> rejects immediately. A handler exception counts as a retry with the
/// exception type as the reason and is always logged.
/// <para>
/// Delivery counting: the broker only adds an <c>x-death</c> entry when a message is actually dead-lettered, so a
/// requeue leaves <see cref="RabbitMqDelivery.DeathCount"/> at zero. The retry bound therefore uses the greater of
/// the <c>x-death</c> count and an in-process attempt count keyed by <c>MessageId</c>, which is what stops a
/// requeue loop from running forever. Deliveries without a <c>MessageId</c> fall back to the <c>x-death</c> count
/// alone and are logged.
/// </para>
/// <para>
/// <see cref="IOptionsMonitor{TOptions}"/> changes to <see cref="RabbitMqConsumerOptions.PrefetchCount"/> take
/// effect on the next channel creation only.
/// </para>
/// </summary>
/// <typeparam name="THandler">Scoped handler type invoked for every delivery.</typeparam>
public sealed class RabbitMqConsumerHostedService<THandler> : BackgroundService
    where THandler : class, IRabbitMqMessageHandler
{
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(25);

    private readonly string _queue;
    private readonly IRabbitMqConnectionMultiplexer _multiplexer;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<RabbitMqOptions> _options;
    private readonly RabbitMqMetrics _metrics;
    private readonly ILogger<RabbitMqConsumerHostedService<THandler>> _logger;
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _deliveryCts = new();
    private RabbitMqConsumerOptions _consumerOptions = new();
    private IChannel? _channel;
    private string? _consumerTag;

    /// <summary>Creates the consumer for one queue.</summary>
    /// <param name="queue">Queue to consume; also the key into <see cref="RabbitMqOptions.Consumers"/>.</param>
    /// <param name="mux">Connection multiplexer that supplies the dedicated consumer channel.</param>
    /// <param name="scopes">Scope factory used to resolve <typeparamref name="THandler"/> per delivery.</param>
    /// <param name="options">Transport options.</param>
    /// <param name="metrics">Instruments for consume counts, duration and the in-flight gauge.</param>
    /// <param name="logger">Logger.</param>
    public RabbitMqConsumerHostedService(
        string queue,
        IRabbitMqConnectionMultiplexer mux,
        IServiceScopeFactory scopes,
        IOptionsMonitor<RabbitMqOptions> options,
        RabbitMqMetrics metrics,
        ILogger<RabbitMqConsumerHostedService<THandler>> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        _queue = queue;
        _multiplexer = mux;
        _scopes = scopes;
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumerOptions = _options.CurrentValue.Consumers.TryGetValue(_queue, out RabbitMqConsumerOptions? configured)
            ? configured
            : new RabbitMqConsumerOptions();

        if (!_consumerOptions.Enabled)
        {
            _logger.ConsumerDisabled(_queue);
            return;
        }

        _channel = await _multiplexer.CreateConsumerChannelAsync(_consumerOptions.PrefetchCount, stoppingToken).ConfigureAwait(false);

        AsyncEventingBasicConsumer consumer = new(_channel);
        consumer.ReceivedAsync += OnDeliveryAsync;

        _consumerTag = await _channel.BasicConsumeAsync(
            _queue, autoAck: false, consumerTag: string.Empty, noLocal: false, exclusive: false,
            arguments: null, consumer: consumer, stoppingToken).ConfigureAwait(false);

        _logger.ConsumerStarted(_queue, _consumerOptions.PrefetchCount, _consumerOptions.MaxDeliveryCount);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the host is shutting down. StopAsync drains the in-flight handlers.
        }
    }

    /// <summary>
    /// Cancels the broker subscription, waits for in-flight handlers within the host's shutdown timeout, then
    /// closes the channel so any delivery that was not acknowledged returns to the queue.
    /// </summary>
    /// <param name="cancellationToken">Bounded by the host's shutdown timeout.</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        IChannel? channel = _channel;

        if (channel is { IsOpen: true } && _consumerTag is not null)
            await channel.BasicCancelAsync(_consumerTag, noWait: false, cancellationToken).ConfigureAwait(false);

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        while (_metrics.ConsumerInFlight(_queue) > 0 && !cancellationToken.IsCancellationRequested)
            await Task.Delay(DrainPollInterval, CancellationToken.None).ConfigureAwait(false);

        int remaining = _metrics.ConsumerInFlight(_queue);
        if (remaining > 0)
            _logger.ShutdownDrainIncomplete(_queue, remaining);

        await _deliveryCts.CancelAsync().ConfigureAwait(false);

        if (channel is not null)
        {
            if (channel.IsOpen)
                await channel.CloseAsync(200, "consumer stopped", false, CancellationToken.None).ConfigureAwait(false);

            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _logger.ConsumerStopped(_queue, remaining);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _deliveryCts.Dispose();
        base.Dispose();
    }

    private async Task OnDeliveryAsync(object sender, BasicDeliverEventArgs args)
    {
        IChannel? channel = _channel;
        if (channel is null)
            return;

        IReadOnlyDictionary<string, object?> headers = RabbitMqHeaders.ToDeliveryHeaders(args.BasicProperties.Headers);

        // The client may recycle the delivery buffer once this callback returns, so the body is copied.
        RabbitMqDelivery delivery = new(
            _queue,
            args.RoutingKey,
            args.BasicProperties.MessageId,
            args.BasicProperties.CorrelationId,
            args.BasicProperties.ContentType,
            headers,
            args.Body.ToArray(),
            args.DeliveryTag,
            args.Redelivered,
            RabbitMqHeaders.DeathCount(headers, _queue));

        _metrics.ConsumerInFlightAdd(_queue, 1);
        long start = Stopwatch.GetTimestamp();
        try
        {
            ConsumeResult result;
            try
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                THandler handler = scope.ServiceProvider.GetRequiredService<THandler>();
                result = await handler.HandleAsync(delivery, _deliveryCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.HandlerFailed(_queue, delivery.DeliveryTag, delivery.MessageId, ex);
                result = ConsumeResult.Retry(ex.GetType().Name);
            }

            await SettleAsync(channel, delivery, result).ConfigureAwait(false);
        }
        finally
        {
            _metrics.ConsumeDuration(_queue, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            _metrics.ConsumerInFlightAdd(_queue, -1);
        }
    }

    private async Task SettleAsync(IChannel channel, RabbitMqDelivery delivery, ConsumeResult result)
    {
        try
        {
            switch (result.Outcome)
            {
                case ConsumeOutcome.Ack:
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None).ConfigureAwait(false);
                    ForgetAttempts(delivery);
                    break;

                case ConsumeOutcome.Reject:
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, CancellationToken.None).ConfigureAwait(false);
                    ForgetAttempts(delivery);
                    _metrics.DeadLettered(_queue);
                    _logger.DeliveryDeadLettered(_queue, delivery.DeliveryTag, delivery.MessageId, delivery.DeathCount + 1, result.Reason);
                    break;

                case ConsumeOutcome.Retry:
                    int attempt = NextAttempt(delivery);
                    bool requeue = attempt < _consumerOptions.MaxDeliveryCount;
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue, CancellationToken.None).ConfigureAwait(false);

                    if (requeue)
                    {
                        _logger.DeliveryRequeued(_queue, delivery.DeliveryTag, delivery.MessageId, attempt, _consumerOptions.MaxDeliveryCount, result.Reason);
                    }
                    else
                    {
                        ForgetAttempts(delivery);
                        _metrics.DeadLettered(_queue);
                        _logger.DeliveryDeadLettered(_queue, delivery.DeliveryTag, delivery.MessageId, attempt, result.Reason);
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Unknown consume outcome.");
            }

            _metrics.Consumed(_queue, result.Outcome);
        }
        catch (Exception ex)
        {
            // Surfaced here rather than rethrown: the client's consumer dispatcher would swallow it. The delivery
            // stays unacknowledged, so the broker redelivers it when the channel closes.
            _logger.SettleFailed(_queue, delivery.DeliveryTag, delivery.MessageId, result.Outcome, ex);
        }
    }

    private int NextAttempt(RabbitMqDelivery delivery)
    {
        if (delivery.MessageId is null)
        {
            _logger.DeliveryWithoutMessageId(_queue, delivery.DeliveryTag);
            return delivery.DeathCount + 1;
        }

        int local = _attempts.AddOrUpdate(delivery.MessageId, 1, (_, current) => current + 1);
        return Math.Max(delivery.DeathCount + 1, local);
    }

    private void ForgetAttempts(RabbitMqDelivery delivery)
    {
        if (delivery.MessageId is not null)
            _attempts.TryRemove(delivery.MessageId, out _);
    }
}
