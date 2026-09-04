# EF.Messaging.RabbitMq - package definition for the EF.* package coding agent

Consumer: scaffold-proof (TaskFlow) and every scaffold-ai generated app that selects `Messaging:Provider = RabbitMq`. Until this package ships, TaskFlow carries the same shapes app-locally in `src/Infrastructure/TaskFlow.Infrastructure.Messaging.RabbitMq` (each site marked `// fallback: replace with EF.Messaging.RabbitMq.<member> when published`); the API below is the contract both must satisfy so the swap is a `using` change.

## Purpose

A thin, framework-free wrapper over `RabbitMQ.Client` 7.x (async API) that gives an app: one persistent connection (multiplexer) with a bounded publisher-channel pool and publisher confirms; idempotent topology declaration; a consumer hosted service with per-queue prefetch (consumer rate limiting), manual ack, bounded requeue and dead-letter exchange routing; options binding, DI registration, health check, OpenTelemetry metrics. It does NOT implement an outbox (the app owns that; see the EF.Data outbox requests) and does not serialize domain events (payload is bytes plus headers).

## Target and dependencies

- `net10.0`; nullable and warnings-as-errors like the other EF.* packages.
- `RabbitMQ.Client` latest 7.x (async-only API: `CreateConnectionAsync`, `IChannel`, `BasicPublishAsync`, `BasicQosAsync`, `BasicAckAsync`, `BasicNackAsync`).
- `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Extensions.Diagnostics.HealthChecks`, `System.Diagnostics.DiagnosticSource` (Meter). No SlimMessageBus, no MassTransit, no Polly (built-in recovery plus bounded loops).
- Must coexist with `Aspire.RabbitMQ.Client`, which registers a singleton `IConnection`: if an `IConnection` is already registered the multiplexer uses it; otherwise it creates one from `RabbitMqOptions.ConnectionString`.

## Public API (namespace `EF.Messaging.RabbitMq`)

```csharp
public sealed class RabbitMqOptions
{
    public string? ConnectionString { get; set; }          // amqp(s)://user:pass@host:port/vhost; ignored when IConnection is registered
    public string ClientProvidedName { get; set; } = "ef-messaging";
    public int PublisherChannelPoolSize { get; set; } = 8;  // bounded pool; RentPublisherChannelAsync waits (async) when exhausted
    public TimeSpan PublisherConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public bool AutomaticRecoveryEnabled { get; set; } = true;
    public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public Dictionary<string, RabbitMqConsumerOptions> Consumers { get; set; } = new(); // keyed by queue name
}

public sealed class RabbitMqConsumerOptions
{
    public ushort PrefetchCount { get; set; } = 16;         // per-consumer channel: BasicQos(0, PrefetchCount, global: false)
    public int MaxDeliveryCount { get; set; } = 5;          // x-death count threshold before reject-to-DLX
    public bool Enabled { get; set; } = true;
}

public sealed record RabbitMqExchange(string Name, string Type = "topic", bool Durable = true, bool AutoDelete = false, IReadOnlyDictionary<string, object?>? Arguments = null);
public sealed record RabbitMqQueue(string Name, bool Durable = true, bool Exclusive = false, bool AutoDelete = false, string? DeadLetterExchange = null, IReadOnlyDictionary<string, object?>? Arguments = null);
public sealed record RabbitMqBinding(string Queue, string Exchange, string RoutingKey);
public sealed record RabbitMqTopology(IReadOnlyList<RabbitMqExchange> Exchanges, IReadOnlyList<RabbitMqQueue> Queues, IReadOnlyList<RabbitMqBinding> Bindings);

public interface IRabbitMqConnectionMultiplexer : IAsyncDisposable
{
    bool IsConnected { get; }
    ValueTask<IConnection> GetConnectionAsync(CancellationToken ct = default);          // lazy, single, thread-safe (SemaphoreSlim(1,1) guarded)
    ValueTask<PooledChannel> RentPublisherChannelAsync(CancellationToken ct = default);  // publisher confirms enabled; return via Dispose
    ValueTask<IChannel> CreateConsumerChannelAsync(ushort prefetchCount, CancellationToken ct = default); // dedicated, QoS applied, not pooled
}

public readonly struct PooledChannel : IDisposable
{
    public IChannel Channel { get; }
    // Dispose returns the channel to the pool; a faulted (closed) channel is discarded, never returned.
}

public sealed record RabbitMqMessage(
    ReadOnlyMemory<byte> Body,
    string RoutingKey,
    string MessageId,
    string ContentType = "application/json",
    string? CorrelationId = null,
    IReadOnlyDictionary<string, object?>? Headers = null,
    bool Persistent = true);

public interface IRabbitMqPublisher
{
    // Publishes all messages on one rented channel, awaits publisher confirms for the whole batch within
    // PublisherConfirmTimeout, and throws RabbitMqPublishException (listing unconfirmed indices) on nack or timeout.
    Task PublishBatchAsync(string exchange, IReadOnlyList<RabbitMqMessage> messages, CancellationToken ct = default);
    Task PublishAsync(string exchange, RabbitMqMessage message, CancellationToken ct = default);
}

public interface IRabbitMqTopologyDeclarer
{
    // Idempotent: ExchangeDeclare / QueueDeclare / QueueBind with passive: false; sets x-dead-letter-exchange when DeadLetterExchange is set.
    Task DeclareAsync(RabbitMqTopology topology, CancellationToken ct = default);
}

public sealed record RabbitMqDelivery(
    string Queue, string RoutingKey, string? MessageId, string? CorrelationId, string? ContentType,
    IReadOnlyDictionary<string, object?> Headers, ReadOnlyMemory<byte> Body, ulong DeliveryTag, bool Redelivered, int DeathCount);

public enum ConsumeOutcome { Ack, Retry, Reject }

public readonly record struct ConsumeResult(ConsumeOutcome Outcome, string? Reason = null)
{
    public static ConsumeResult Ack => new(ConsumeOutcome.Ack);
    public static ConsumeResult Retry(string reason) => new(ConsumeOutcome.Retry, reason);
    public static ConsumeResult Reject(string reason) => new(ConsumeOutcome.Reject, reason); // dead-letter now
}

public interface IRabbitMqMessageHandler
{
    Task<ConsumeResult> HandleAsync(RabbitMqDelivery delivery, CancellationToken ct);
}

// One hosted service per queue. Concurrency == PrefetchCount. Each delivery runs the handler inside a new
// IServiceScope so scoped handlers are safe. Ack after the handler returns Ack.
// Retry: BasicNack(requeue: DeathCount < MaxDeliveryCount); once the threshold is reached the service rejects instead
// (requeue: false) so the broker routes the message to the queue's DLX.
// Reject: BasicNack(requeue: false); the broker routes to the DLX. Because a nack cannot add headers, the service
// first publishes a copy carrying x-ef-reject-reason to the DLX ONLY when the queue has no DLX configured (documented edge);
// with a DLX configured it relies on the broker's x-death entry and logs the reason.
// Handler exceptions count as Retry with the exception type as reason; never swallowed (Warning log with delivery tag and MessageId).
public sealed class RabbitMqConsumerHostedService<THandler> : BackgroundService where THandler : class, IRabbitMqMessageHandler
{
    public RabbitMqConsumerHostedService(
        string queue,
        IRabbitMqConnectionMultiplexer mux,
        IServiceScopeFactory scopes,
        IOptionsMonitor<RabbitMqOptions> options,
        RabbitMqMetrics metrics,
        ILogger<RabbitMqConsumerHostedService<THandler>> logger);
}

public static class RabbitMqServiceCollectionExtensions
{
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, IConfiguration configuration, string sectionName = "Messaging:RabbitMq");

    // Registers THandler (scoped) and the hosted service; skipped when Consumers[queue].Enabled == false.
    public static IServiceCollection AddRabbitMqConsumer<THandler>(this IServiceCollection services, string queue) where THandler : class, IRabbitMqMessageHandler;

    // Declared once by RabbitMqTopologyStartup (IHostedService) before any consumer starts.
    public static IServiceCollection AddRabbitMqTopology(this IServiceCollection services, RabbitMqTopology topology);

    public static IHealthChecksBuilder AddRabbitMqHealthCheck(this IHealthChecksBuilder builder, string name = "rabbitmq", params string[] tags);
}

public sealed class RabbitMqMetrics // Meter "EF.Messaging.RabbitMq"
{
    // counters: ef.rabbitmq.published (exchange), ef.rabbitmq.publish.nacked, ef.rabbitmq.consumed (queue, outcome), ef.rabbitmq.deadlettered (queue)
    // histograms: ef.rabbitmq.publish.confirm.duration (ms), ef.rabbitmq.consume.duration (ms, queue)
    // observable gauges: ef.rabbitmq.consumer.inflight (queue), ef.rabbitmq.publisher.pool.rented
}

public sealed class RabbitMqPublishException : Exception
{
    public IReadOnlyList<int> UnconfirmedIndices { get; }
}
```

## Behavior contracts

1. Multiplexer: exactly one `IConnection` per process per options instance; creation is lazy and serialized; `AutomaticRecoveryEnabled` and `TopologyRecoveryEnabled` default true; connection shutdown events are logged at Warning and counted. Publisher channels are created with confirms enabled (`CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)`); a channel closed by the broker is disposed and replaced, never returned to the pool.
2. Publisher: a batch is published on one channel, then confirms are awaited once for the batch within `PublisherConfirmTimeout`; any nack or timeout throws `RabbitMqPublishException` with the unconfirmed indices. Properties set: `MessageId`, `CorrelationId`, `ContentType`, `Persistent` (delivery mode 2), `Timestamp` (UTC now); headers copied verbatim (string, int, long, bool, byte[] supported; other types throw `ArgumentException` before any publish).
3. Topology: idempotent declares; a mismatch with an existing declaration surfaces the broker's `PRECONDITION_FAILED` as an exception (never caught). Queue `DeadLetterExchange` maps to argument `x-dead-letter-exchange`.
4. Consumer: `BasicQosAsync(0, PrefetchCount, false)` before `BasicConsumeAsync(autoAck: false)`; `DeathCount` is the sum of `count` entries in the `x-death` header for this queue; outcomes as commented above; on cancellation the service stops consuming, waits for in-flight handlers up to `HostOptions.ShutdownTimeout`, then closes the channel. `IOptionsMonitor` changes to `PrefetchCount` apply on the next channel creation only (documented).
5. Options validation with `ValidateOnStart`: `PublisherChannelPoolSize >= 1`, every `PrefetchCount >= 1`, every `MaxDeliveryCount >= 1`, a connection source present (registered `IConnection` or `ConnectionString`).
6. No silent failure anywhere: every dropped, nacked, or dead-lettered message produces a log line with MessageId and a metric.

## Acceptance tests (ship in the package test project; Testcontainers.RabbitMq, image `rabbitmq:4-management`)

- Multiplexer: 50 concurrent `RentPublisherChannelAsync` calls with pool size 8 never exceed 8 open publisher channels (management API); a channel closed by the broker (publish to a missing exchange) is not returned to the pool and the next rent succeeds.
- Publisher: a 1000-message batch is confirmed within the timeout; publishing to a non-existent exchange throws `RabbitMqPublishException`; MessageId, CorrelationId, ContentType, headers, and the persistent flag round-trip.
- Topology: `DeclareAsync` twice is a no-op; declaring an existing queue with different arguments throws.
- Consumer: with `PrefetchCount = 4` and a blocking handler, at most 4 deliveries are in flight (management API or the inflight gauge); `Reject` lands in the DLX queue; `Retry` is redelivered `MaxDeliveryCount` times, then dead-lettered; a handler exception counts as Retry; a scoped handler receives a fresh scope per delivery; graceful shutdown acks nothing twice and loses nothing (unacked deliveries return to the queue).
- Health check: Healthy while connected, Unhealthy after the container stops.
- Coexistence: with `Aspire.RabbitMQ.Client` registering `IConnection`, no second connection is opened (connection count via management API).

## What TaskFlow will do with it

- `RabbitMqEventTransport : IIntegrationEventTransport` maps `OutboxMessage` to `RabbitMqMessage` (RoutingKey = EventType, MessageId = outbox row Id, headers EventType/EventVersion/TenantId/CorrelationId) and calls `PublishBatchAsync("taskflow.domain-events", ...)`; a publish failure releases the outbox lease.
- Topology: exchange `taskflow.domain-events` (topic), DLX `taskflow.domain-events.dlx` with queue `taskflow.dead-letter`, queues `taskflow.projection`, `taskflow.ai-review`, `taskflow.workflow` bound by routing key per event type.
- Three `AddRabbitMqConsumer<T>` registrations in the Scheduler host when `Messaging:Provider == RabbitMq`; the handlers wrap the same consumer logic the Azure Functions Service Bus triggers call, plus the `ConsumerInbox` idempotency guard.
- Config: `Messaging:RabbitMq:Consumers:{ "taskflow.projection": { PrefetchCount: 16 }, "taskflow.ai-review": { PrefetchCount: 4 }, "taskflow.workflow": { PrefetchCount: 8 } }`; connection via the Aspire connection name `RabbitMq1`.

## Out of scope for the package

Outbox and inbox tables (EF.Data requests), envelope and versioning (EF.Messaging core request 14), Azure Service Bus (existing EF.Messaging), delayed retry via TTL wait queues (document as an extension point: a `RetryExchange` option publishing to a per-queue wait queue with `x-message-ttl`; not in v1).
