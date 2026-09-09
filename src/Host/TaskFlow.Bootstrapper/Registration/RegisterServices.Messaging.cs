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
    public const string MessagingProviderConfigKey = "Messaging:Provider";

    /// <summary>Environment variable that overrides the configured transport, mirroring Database:Provider.</summary>
    public const string MessagingProviderEnvVar = "TASKFLOW_MESSAGING_PROVIDER";

    /// <summary>
    /// Resolves the transport once for the process. The environment variable wins so a test lane or a container
    /// can flip providers without editing configuration; when neither is set, the Portable lane defaults to
    /// RabbitMQ and the Azure lane keeps today's Service Bus default (D-035).
    /// </summary>
    public static MessagingProvider ResolveMessagingProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var value = Environment.GetEnvironmentVariable(MessagingProviderEnvVar) ?? config[MessagingProviderConfigKey];
        if (!string.IsNullOrWhiteSpace(value)) return ParseMessagingProvider(value);

        return HostingLaneSelector.Resolve(config) == HostingLane.Portable
            ? MessagingProvider.RabbitMq
            : MessagingProvider.ServiceBus;
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
