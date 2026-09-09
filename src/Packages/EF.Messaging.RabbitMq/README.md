# EF.Messaging.RabbitMq

A thin, framework-free wrapper over `RabbitMQ.Client` 7.x (async API only). It gives an application one persistent
connection with a bounded publisher-channel pool and publisher confirms, idempotent topology declaration, and a
consumer hosted service with per-queue prefetch, manual acknowledgement, a bounded requeue and dead-letter routing -
plus options binding, DI registration, a health check and OpenTelemetry metrics.

No outbox, no serialization: the payload is bytes plus headers. No Polly, SlimMessageBus or MassTransit.

## Registration

```csharp
builder.Services.AddRabbitMqMessaging(builder.Configuration);            // section "Messaging:RabbitMq"
builder.Services.AddRabbitMqTopology(new RabbitMqTopology(
    Exchanges: [new RabbitMqExchange("app.events")],
    Queues:    [new RabbitMqQueue("app.projection", DeadLetterExchange: "app.events.dlx")],
    Bindings:  [new RabbitMqBinding("app.projection", "app.events", "task.*")]));
builder.Services.AddRabbitMqConsumer<ProjectionHandler>("app.projection");
builder.Services.AddHealthChecks().AddRabbitMqHealthCheck(tags: "ready");
```

Add meter `EF.Messaging.RabbitMq` to the OpenTelemetry metrics pipeline to export the instruments.

`AddRabbitMqTopology` inserts its hosted service at the front of the service collection so the topology exists
before any consumer subscribes, whatever order the application registers in.

## Options (`Messaging:RabbitMq`)

| Key | Default | Meaning |
|---|---|---|
| `ConnectionString` | none | `amqp(s)://user:pass@host:port/vhost`. Ignored when an `IConnection` is already registered. |
| `ClientProvidedName` | `ef-messaging` | Connection name shown in the management UI. |
| `PublisherChannelPoolSize` | `8` | Upper bound on concurrently rented publisher channels; renting waits when exhausted. Read once at construction. |
| `PublisherConfirmTimeout` | `00:00:10` | Confirm window for a whole publish batch. |
| `AutomaticRecoveryEnabled` | `true` | Client automatic connection recovery. |
| `NetworkRecoveryInterval` | `00:00:05` | Recovery retry interval. |
| `Consumers:<queue>:PrefetchCount` | `16` | `BasicQos(0, n, global: false)`; also the consumer dispatch concurrency. |
| `Consumers:<queue>:MaxDeliveryCount` | `5` | Delivery attempts before a retried message is dead-lettered. |
| `Consumers:<queue>:Enabled` | `true` | When false the hosted service starts no consumer. |

Validation runs with `ValidateOnStart`: pool size and every prefetch and delivery count must be at least 1, and a
connection source must exist (a registered `IConnection` or a connection string).

## Coexistence with `Aspire.RabbitMQ.Client`

Aspire's client integration registers a singleton `IConnection`. The multiplexer uses that connection when one is
registered and never disposes it; only a connection it created itself is closed on shutdown. No Aspire package is
referenced here.

## Publishing

```csharp
await publisher.PublishBatchAsync("app.events",
    [new RabbitMqMessage(payload, RoutingKey: "task.created", MessageId: id.ToString(),
        Headers: new Dictionary<string, object?> { ["EventType"] = "TaskCreated" })]);
```

The batch goes out on one channel and is awaited once. Any nack, return or timeout throws
`RabbitMqPublishException` carrying the zero-based `UnconfirmedIndices` of the batch. Header values may be
`string`, `int`, `long`, `bool`, `byte[]` or null; anything else throws `ArgumentException` before a single message
is published. AMQP encodes string headers as UTF-8 bytes, so a consumer reads them back as `byte[]`.

## Consuming

```csharp
public sealed class ProjectionHandler(AppDbContext db) : IRabbitMqMessageHandler
{
    public async Task<ConsumeResult> HandleAsync(RabbitMqDelivery delivery, CancellationToken ct)
    {
        if (delivery.ContentType != "application/json")
            return ConsumeResult.Reject("unsupported content type");   // dead-letter now

        await db.ApplyAsync(delivery.Body, ct);
        return ConsumeResult.Ack;
    }
}
```

Concurrency equals `PrefetchCount`, and each delivery resolves the handler from a fresh scope. `Retry` requeues
until the delivery bound is reached and then rejects so the broker routes the message to the queue's dead-letter
exchange; a handler exception is logged and counts as `Retry`. On shutdown the consumer cancels its subscription,
waits for in-flight handlers within the host's shutdown timeout, then closes the channel, so anything not
acknowledged returns to the queue.

Delivery counting: the broker only writes an `x-death` entry when a message is really dead-lettered, so a plain
requeue leaves `RabbitMqDelivery.DeathCount` at zero. The retry bound therefore uses the greater of the `x-death`
count and an in-process attempt count keyed by `MessageId`; without that, a requeue loop would never end. Publish
with a `MessageId` (the transport always sets one) to get the bound. `PrefetchCount` changes from
`IOptionsMonitor` apply on the next channel creation only.

`ConsumeResult.Reject(reason)` cannot attach the reason to the dead-lettered message: AMQP `basic.nack` carries no
headers. The reason is logged with the queue, delivery tag and `MessageId`, and the broker's own `x-death` entry
records the rejection.

## Porting note

This project is developed inside the TaskFlow reference repository so it has a real call site, but it references
no TaskFlow project or type and takes no Aspire dependency. Move `src/Packages/EF.Messaging.RabbitMq` and
`tests/EF.Messaging.RabbitMq.Tests` into the EF.* package repository unchanged; only the central package versions
(`RabbitMQ.Client`, `Testcontainers.RabbitMq`, the `Microsoft.Extensions.*` family) need to come across.
