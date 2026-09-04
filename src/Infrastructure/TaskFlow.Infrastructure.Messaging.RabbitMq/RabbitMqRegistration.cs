using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Infrastructure.Data.Messaging;

namespace TaskFlow.Infrastructure.Messaging.RabbitMq;

/// <summary>Composition of the RabbitMQ transport and its consumers (D-034).</summary>
public static class RabbitMqRegistration
{
    /// <summary>Configuration section the package binds its options from.</summary>
    public const string OptionsSection = "Messaging:RabbitMq";

    // Per-queue defaults: projection is the cheapest and highest volume, AI review is the slowest and most
    // expensive per message, workflow starts sit in between. Overridable per queue in configuration.
    private static readonly (string Queue, ushort Prefetch)[] QueueDefaults =
    [
        (TaskFlowRabbitMqTopology.ProjectionQueue, 16),
        (TaskFlowRabbitMqTopology.AiReviewQueue, 4),
        (TaskFlowRabbitMqTopology.WorkflowQueue, 8)
    ];

    /// <summary>
    /// Registers the package core and the publisher-side transport. Every host that stages outbox rows calls
    /// this through the provider switch; only the consumer host adds <see cref="AddTaskFlowRabbitMqConsumers"/>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="config">Configuration root.</param>
    public static IServiceCollection AddTaskFlowRabbitMqMessaging(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddRabbitMqMessaging(config, OptionsSection);
        services.AddSingleton<IIntegrationEventTransport, RabbitMqEventTransport>();
        return services;
    }

    /// <summary>
    /// Declares the topology and starts the three consumer hosted services with their per-queue prefetch, plus
    /// the broker health check. Only the Scheduler calls this: the Functions runtime has no RabbitMQ trigger.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="config">Configuration root.</param>
    public static IServiceCollection AddTaskFlowRabbitMqConsumers(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.Configure<RabbitMqOptions>(options =>
        {
            foreach (var (queue, prefetch) in QueueDefaults)
            {
                if (!options.Consumers.TryGetValue(queue, out var consumer))
                    options.Consumers[queue] = consumer = new RabbitMqConsumerOptions();

                // Configuration wins: only fill in the per-queue default when the section did not set one.
                var configured = config[$"{OptionsSection}:Consumers:{queue}:PrefetchCount"];
                if (string.IsNullOrWhiteSpace(configured)) consumer.PrefetchCount = prefetch;
            }
        });

        services.AddRabbitMqTopology(TaskFlowRabbitMqTopology.Build());
        services.AddRabbitMqConsumer<RabbitMqProjectionHandler>(TaskFlowRabbitMqTopology.ProjectionQueue);
        services.AddRabbitMqConsumer<RabbitMqAiReviewHandler>(TaskFlowRabbitMqTopology.AiReviewQueue);
        services.AddRabbitMqConsumer<RabbitMqWorkflowHandler>(TaskFlowRabbitMqTopology.WorkflowQueue);
        services.AddHealthChecks().AddRabbitMqHealthCheck(tags: "ready");

        return services;
    }
}
