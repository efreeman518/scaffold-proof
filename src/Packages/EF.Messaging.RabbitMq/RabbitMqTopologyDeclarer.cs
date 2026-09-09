using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// Declares exchanges, queues and bindings on a pooled channel. Declarations are idempotent; a conflicting
/// redeclaration closes the channel with <c>PRECONDITION_FAILED</c> and the exception is left to propagate.
/// </summary>
internal sealed class RabbitMqTopologyDeclarer(
    IRabbitMqConnectionMultiplexer multiplexer,
    ILogger<RabbitMqTopologyDeclarer> logger) : IRabbitMqTopologyDeclarer
{
    private const string DeadLetterExchangeArgument = "x-dead-letter-exchange";

    public async Task DeclareAsync(RabbitMqTopology topology, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(topology);

        using PooledChannel rented = await multiplexer.RentPublisherChannelAsync(ct).ConfigureAwait(false);
        IChannel channel = rented.Channel;

        foreach (RabbitMqExchange exchange in topology.Exchanges)
        {
            await channel.ExchangeDeclareAsync(
                exchange.Name, exchange.Type, exchange.Durable, exchange.AutoDelete,
                RabbitMqHeaders.ToArguments(exchange.Arguments), passive: false, noWait: false, ct).ConfigureAwait(false);
        }

        foreach (RabbitMqQueue queue in topology.Queues)
        {
            Dictionary<string, object?>? arguments = RabbitMqHeaders.ToArguments(queue.Arguments);
            if (queue.DeadLetterExchange is not null)
            {
                arguments ??= [];
                arguments[DeadLetterExchangeArgument] = queue.DeadLetterExchange;
            }

            await channel.QueueDeclareAsync(
                queue.Name, queue.Durable, queue.Exclusive, queue.AutoDelete,
                arguments, passive: false, noWait: false, ct).ConfigureAwait(false);
        }

        foreach (RabbitMqBinding binding in topology.Bindings)
        {
            await channel.QueueBindAsync(
                binding.Queue, binding.Exchange, binding.RoutingKey,
                arguments: null, noWait: false, ct).ConfigureAwait(false);
        }

        logger.TopologyDeclared(topology.Exchanges.Count, topology.Queues.Count, topology.Bindings.Count);
    }
}

/// <summary>Declares the application topology once, before any consumer hosted service starts.</summary>
internal sealed class RabbitMqTopologyStartup(RabbitMqTopology topology, IRabbitMqTopologyDeclarer declarer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => declarer.DeclareAsync(topology, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
