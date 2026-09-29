using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.MessageHandlers.Consumers;
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

        // The package startup inserts itself at the front so the topology exists before any consumer
        // subscribes. Every replica declares, unlocked: identical declarations are idempotent, and the
        // PRECONDITION_FAILED a changed definition raises happens whether or not two declarations overlap in
        // time, so a distributed lock around it only added a startup wait without preventing anything.
        services.AddRabbitMqTopology(TaskFlowRabbitMqTopology.Build(includeEmbedding));

        // M15: the package adapter maps each delivery onto the same consumer the Service Bus triggers run (D-034):
        // unreadable body -> dead-letter, consumed/duplicate -> ack, claim still in progress -> retry.
        services.AddRabbitMqConsumer<RabbitMqIntegrationEventHandler<TaskProjectionConsumer>>(TaskFlowRabbitMqTopology.ProjectionQueue);
        services.AddRabbitMqConsumer<RabbitMqIntegrationEventHandler<TaskAiReviewConsumer>>(TaskFlowRabbitMqTopology.AiReviewQueue);
        services.AddRabbitMqConsumer<RabbitMqIntegrationEventHandler<TaskWorkflowConsumer>>(TaskFlowRabbitMqTopology.WorkflowQueue);
        if (includeEmbedding)
            services.AddRabbitMqConsumer<RabbitMqIntegrationEventHandler<TaskEmbeddingConsumer>>(TaskFlowRabbitMqTopology.EmbeddingQueue);
        services.AddHealthChecks().AddRabbitMqHealthCheck(tags: "ready");

        return services;
    }
}
