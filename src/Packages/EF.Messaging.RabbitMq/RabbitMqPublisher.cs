using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Diagnostics;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// Publishes batches on one pooled channel with publisher confirms. Confirm tracking makes each
/// <c>BasicPublishAsync</c> complete only when the broker confirms it, so a batch is published first and awaited
/// afterwards under a single <see cref="RabbitMqOptions.PublisherConfirmTimeout"/>.
/// </summary>
internal sealed class RabbitMqPublisher(
    IRabbitMqConnectionMultiplexer multiplexer,
    IOptionsMonitor<RabbitMqOptions> options,
    RabbitMqMetrics metrics,
    ILogger<RabbitMqPublisher> logger) : IRabbitMqPublisher
{
    public Task PublishAsync(string exchange, RabbitMqMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PublishBatchAsync(exchange, [message], ct);
    }

    public async Task PublishBatchAsync(string exchange, IReadOnlyList<RabbitMqMessage> messages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
            return;

        // Validated up front so an unsupported header type cannot leave a batch half published.
        foreach (RabbitMqMessage message in messages)
            RabbitMqHeaders.Validate(message.Headers);

        long start = Stopwatch.GetTimestamp();
        using PooledChannel rented = await multiplexer.RentPublisherChannelAsync(ct).ConfigureAwait(false);
        using CancellationTokenSource confirmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        confirmCts.CancelAfter(options.CurrentValue.PublisherConfirmTimeout);

        Task[] confirms = new Task[messages.Count];
        for (int i = 0; i < messages.Count; i++)
        {
            RabbitMqMessage message = messages[i];
            try
            {
                confirms[i] = rented.Channel
                    .BasicPublishAsync(exchange, message.RoutingKey, mandatory: false, CreateProperties(message), message.Body, confirmCts.Token)
                    .AsTask();
            }
            catch (Exception ex)
            {
                // A broker-closed channel faults every remaining publish; record the position and keep the indices aligned.
                confirms[i] = Task.FromException(ex);
            }
        }

        List<int> unconfirmed = [];
        Exception? firstFailure = null;
        for (int i = 0; i < confirms.Length; i++)
        {
            try
            {
                await confirms[i].ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                unconfirmed.Add(i);
                firstFailure ??= ex;
            }
        }

        metrics.PublishConfirmDuration(exchange, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        metrics.Published(exchange, messages.Count - unconfirmed.Count);

        if (unconfirmed.Count == 0)
            return;

        metrics.PublishNacked(exchange, unconfirmed.Count);
        ct.ThrowIfCancellationRequested();

        string firstMessageId = messages[unconfirmed[0]].MessageId;
        logger.PublishBatchUnconfirmed(exchange, unconfirmed.Count, messages.Count, firstMessageId, firstFailure);
        throw new RabbitMqPublishException(
            $"{unconfirmed.Count} of {messages.Count} messages published to exchange '{exchange}' were not confirmed within {options.CurrentValue.PublisherConfirmTimeout}.",
            unconfirmed,
            firstFailure);
    }

    private static BasicProperties CreateProperties(RabbitMqMessage message)
    {
        BasicProperties properties = new()
        {
            MessageId = message.MessageId,
            ContentType = message.ContentType,
            Persistent = message.Persistent,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        if (message.CorrelationId is not null)
            properties.CorrelationId = message.CorrelationId;

        if (message.Headers is { Count: > 0 })
            properties.Headers = RabbitMqHeaders.ToBrokerHeaders(message.Headers);

        return properties;
    }
}
