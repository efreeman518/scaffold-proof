using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using System.Diagnostics;

namespace TaskFlow.Observability.Tracing;

/// <summary>
/// W3C trace context across the broker hop (D-053). Without this, every message starts a new root trace and
/// the request that produced it is unreachable from the consumer that handled it.
/// <para>
/// The carriers are passed as delegates rather than dictionaries on purpose: RabbitMQ headers are
/// <c>object?</c> valued and arrive as UTF-8 byte arrays, Service Bus application properties are
/// <c>object</c> valued strings, and neither dictionary type converts to the other.
/// </para>
/// </summary>
public static class MessagingTrace
{
    /// <summary><c>messaging.system</c> value for the RabbitMQ transport.</summary>
    public const string RabbitMqSystem = "rabbitmq";

    /// <summary><c>messaging.system</c> value for the Azure Service Bus transport.</summary>
    public const string ServiceBusSystem = "servicebus";

    /// <summary>
    /// Starts the producer span for one message and injects the resulting trace context through
    /// <paramref name="setHeader"/>. Injection happens even when no listener is sampling the source, because
    /// the ambient request context is what the consumer needs and it exists either way.
    /// </summary>
    /// <param name="system">Broker identity for <c>messaging.system</c>.</param>
    /// <param name="destination">Queue, topic or exchange name.</param>
    /// <param name="eventType">Integration event type; also the span name prefix.</param>
    /// <param name="messageId">Broker message id (the outbox row id).</param>
    /// <param name="setHeader">Writes one header on the outgoing message.</param>
    /// <returns>The producer activity, or null when nothing is listening. Dispose it after the send.</returns>
    public static Activity? StartPublish(
        string system,
        string destination,
        string eventType,
        string messageId,
        Action<string, string> setHeader)
    {
        ArgumentNullException.ThrowIfNull(setHeader);

        var activity = TaskFlowActivitySources.Messaging.StartActivity(
            $"{eventType} publish", ActivityKind.Producer);

        activity?.SetTag("messaging.system", system)
            .SetTag("messaging.operation.name", "publish")
            .SetTag("messaging.destination.name", destination)
            .SetTag("messaging.message.id", messageId);

        var context = activity is null
            ? new PropagationContext(Activity.Current?.Context ?? default, Baggage.Current)
            : new PropagationContext(activity.Context, Baggage.Current);

        if (context.ActivityContext != default)
        {
            Propagators.DefaultTextMapPropagator.Inject(context, setHeader, static (set, key, value) => set(key, value));
        }

        return activity;
    }

    /// <summary>
    /// Starts the consumer span for one delivery, parented to the producer's context.
    /// <para>
    /// Parent, not <see cref="ActivityLink"/>: the outbox hop is one business operation continuing across a
    /// process boundary, so a single trace from the HTTP request through the consumer is the whole point. A
    /// link would leave the consumer as its own root - which is exactly the disconnected state D-053 fixes -
    /// and is the right shape only for a batch whose items come from many unrelated traces.
    /// </para>
    /// </summary>
    /// <param name="system">Broker identity for <c>messaging.system</c>.</param>
    /// <param name="destination">Queue or subscription the message was delivered from.</param>
    /// <param name="eventType">Integration event type; also the span name prefix.</param>
    /// <param name="messageId">Broker message id, when the delivery carries one.</param>
    /// <param name="getHeader">Reads one header from the delivery, or null when absent.</param>
    /// <returns>The consumer activity, or null when nothing is listening.</returns>
    public static Activity? StartProcess(
        string system,
        string destination,
        string eventType,
        string? messageId,
        Func<string, string?> getHeader)
    {
        ArgumentNullException.ThrowIfNull(getHeader);

        var parent = Propagators.DefaultTextMapPropagator.Extract(
            default,
            getHeader,
            static (get, key) =>
            {
                var value = get(key);
                return value is null ? null : [value];
            });

        Baggage.Current = parent.Baggage;

        var activity = TaskFlowActivitySources.Messaging.StartActivity(
            $"{eventType} process", ActivityKind.Consumer, parent.ActivityContext);

        activity?.SetTag("messaging.system", system)
            .SetTag("messaging.operation.name", "process")
            .SetTag("messaging.destination.name", destination)
            .SetTag("messaging.message.id", messageId);

        return activity;
    }
}
