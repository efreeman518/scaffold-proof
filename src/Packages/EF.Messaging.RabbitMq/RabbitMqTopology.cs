namespace EF.Messaging.RabbitMq;

/// <summary>An exchange to declare.</summary>
/// <param name="Name">Exchange name.</param>
/// <param name="Type">AMQP exchange type (<c>topic</c>, <c>direct</c>, <c>fanout</c>, <c>headers</c>).</param>
/// <param name="Durable">Survives a broker restart.</param>
/// <param name="AutoDelete">Deleted when the last binding is removed.</param>
/// <param name="Arguments">Extra broker arguments.</param>
public sealed record RabbitMqExchange(
    string Name,
    string Type = "topic",
    bool Durable = true,
    bool AutoDelete = false,
    IReadOnlyDictionary<string, object?>? Arguments = null);

/// <summary>A queue to declare.</summary>
/// <param name="Name">Queue name.</param>
/// <param name="Durable">Survives a broker restart.</param>
/// <param name="Exclusive">Restricted to the declaring connection.</param>
/// <param name="AutoDelete">Deleted when the last consumer disconnects.</param>
/// <param name="DeadLetterExchange">Sets the <c>x-dead-letter-exchange</c> argument when not null.</param>
/// <param name="Arguments">Extra broker arguments; merged with the dead-letter argument.</param>
public sealed record RabbitMqQueue(
    string Name,
    bool Durable = true,
    bool Exclusive = false,
    bool AutoDelete = false,
    string? DeadLetterExchange = null,
    IReadOnlyDictionary<string, object?>? Arguments = null);

/// <summary>A queue-to-exchange binding.</summary>
/// <param name="Queue">Queue name.</param>
/// <param name="Exchange">Exchange name.</param>
/// <param name="RoutingKey">Binding routing key or pattern.</param>
public sealed record RabbitMqBinding(string Queue, string Exchange, string RoutingKey);

/// <summary>The exchanges, queues and bindings an application declares at startup.</summary>
/// <param name="Exchanges">Exchanges, declared first.</param>
/// <param name="Queues">Queues, declared after the exchanges.</param>
/// <param name="Bindings">Bindings, declared last.</param>
public sealed record RabbitMqTopology(
    IReadOnlyList<RabbitMqExchange> Exchanges,
    IReadOnlyList<RabbitMqQueue> Queues,
    IReadOnlyList<RabbitMqBinding> Bindings);

/// <summary>Declares a <see cref="RabbitMqTopology"/> on the broker.</summary>
public interface IRabbitMqTopologyDeclarer
{
    /// <summary>
    /// Declares every exchange, queue and binding. Declarations are idempotent; a declaration that conflicts with
    /// an existing definition surfaces the broker's <c>PRECONDITION_FAILED</c> as an exception.
    /// </summary>
    /// <param name="topology">The topology to declare.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeclareAsync(RabbitMqTopology topology, CancellationToken ct = default);
}
