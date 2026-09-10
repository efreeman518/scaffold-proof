using EF.Common.Contracts;
using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskFlow.Infrastructure.Data.Messaging;

namespace TaskFlow.Infrastructure.Messaging.RabbitMq;

/// <summary>Composition of the RabbitMQ transport and its consumers (D-034).</summary>
public static class RabbitMqRegistration
{
    /// <summary>Configuration section the package binds its options from.</summary>
    public const string OptionsSection = "Messaging:RabbitMq";

    // Per-queue defaults: projection is the cheapest and highest volume, AI review is the slowest and most
    // expensive per message, workflow starts sit in between. Embedding sits with AI review: every message is
    // a model call. Overridable per queue in configuration.
    private static readonly (string Queue, ushort Prefetch)[] QueueDefaults =
    [
        (TaskFlowRabbitMqTopology.ProjectionQueue, 16),
        (TaskFlowRabbitMqTopology.AiReviewQueue, 4),
        (TaskFlowRabbitMqTopology.WorkflowQueue, 8),
        (TaskFlowRabbitMqTopology.EmbeddingQueue, 4)
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
    /// Declares the topology and starts the consumer hosted services with their per-queue prefetch, plus the
    /// broker health check. Only the Scheduler calls this: the Functions runtime has no RabbitMQ trigger.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="config">Configuration root.</param>
    /// <param name="includeEmbedding">
    /// True only when <c>Search:Provider</c> resolves to PgVector (D-040). The caller resolves it because the
    /// search switch lives in Infrastructure.AI and this transport adapter must not depend on it.
    /// </param>
    public static IServiceCollection AddTaskFlowRabbitMqConsumers(
        this IServiceCollection services, IConfiguration config, bool includeEmbedding = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.Configure<RabbitMqOptions>(options =>
        {
            foreach (var (queue, prefetch) in QueueDefaults)
            {
                // No prefetch entry for a queue this deployment neither declares nor consumes.
                if (!includeEmbedding && queue == TaskFlowRabbitMqTopology.EmbeddingQueue) continue;

                if (!options.Consumers.TryGetValue(queue, out var consumer))
                    options.Consumers[queue] = consumer = new RabbitMqConsumerOptions();

                // Configuration wins: only fill in the per-queue default when the section did not set one.
                var configured = config[$"{OptionsSection}:Consumers:{queue}:PrefetchCount"];
                if (string.IsNullOrWhiteSpace(configured)) consumer.PrefetchCount = prefetch;
            }
        });

        // D-052: TaskFlow's own topology startup instead of the package's, so declaration is serialized
        // across replicas by the distributed lock. Inserted at the front for the same reason the package
        // inserts its own there - the topology has to exist before any consumer subscribes.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(sp => new TaskFlowRabbitMqTopologyStartup(
            TaskFlowRabbitMqTopology.Build(includeEmbedding),
            sp.GetRequiredService<IRabbitMqTopologyDeclarer>(),
            sp.GetRequiredService<IDistributedLock>(),
            sp.GetRequiredService<ILogger<TaskFlowRabbitMqTopologyStartup>>())));

        services.AddRabbitMqConsumer<RabbitMqProjectionHandler>(TaskFlowRabbitMqTopology.ProjectionQueue);
        services.AddRabbitMqConsumer<RabbitMqAiReviewHandler>(TaskFlowRabbitMqTopology.AiReviewQueue);
        services.AddRabbitMqConsumer<RabbitMqWorkflowHandler>(TaskFlowRabbitMqTopology.WorkflowQueue);
        if (includeEmbedding)
            services.AddRabbitMqConsumer<RabbitMqEmbeddingHandler>(TaskFlowRabbitMqTopology.EmbeddingQueue);
        services.AddHealthChecks().AddRabbitMqHealthCheck(tags: "ready");

        return services;
    }
}
