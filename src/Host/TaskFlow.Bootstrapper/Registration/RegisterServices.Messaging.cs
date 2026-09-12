using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Messaging.RabbitMq;

namespace TaskFlow.Bootstrapper;

/// <summary>Messaging transport selected for this deployment (D-034).</summary>
public enum MessagingProvider
{
    /// <summary>Azure Service Bus topic with per-consumer subscriptions.</summary>
    ServiceBus,

    /// <summary>RabbitMQ topic exchange with per-consumer queues.</summary>
    RabbitMq
}

/// <summary>Configures the messaging transport for TaskFlow runtime hosts.</summary>
public static partial class RegisterServices
{
    /// <summary>Configuration key selecting the transport.</summary>
    public const string MessagingProviderConfigKey = HostingLaneResolver.MessagingConfigurationKey;

    /// <summary>Environment variable that overrides the configured transport, mirroring Database:Provider.</summary>
    public const string MessagingProviderEnvVar = HostingLaneResolver.MessagingEnvironmentVariable;

    /// <summary>
    /// Resolves the strict lane's transport through the shared D-060 contract.
    /// </summary>
    public static MessagingProvider ResolveMessagingProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ParseMessagingProvider(HostingLaneResolver.Resolve(config).Messaging);
    }

    private static MessagingProvider ParseMessagingProvider(string value) =>
        Enum.TryParse<MessagingProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown messaging provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<MessagingProvider>())}.");

    /// <summary>Registers the outbox transport for the selected provider. Consumers are hosted separately.</summary>
    [ProviderSwitch(typeof(IIntegrationEventTransport))]
    private static void AddMessagingServices(IServiceCollection services, IConfiguration config)
    {
        if (ResolveMessagingProvider(config) == MessagingProvider.RabbitMq)
        {
            services.AddTaskFlowRabbitMqMessaging(config);
            return;
        }

        AddServiceBusServices(services, config);
    }
}
