using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    /// can flip providers without editing configuration; an unrecognized value falls back to Service Bus.
    /// </summary>
    public static MessagingProvider ResolveMessagingProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var value = Environment.GetEnvironmentVariable(MessagingProviderEnvVar);
        if (string.IsNullOrWhiteSpace(value)) value = config[MessagingProviderConfigKey];

        return string.Equals(value, nameof(MessagingProvider.RabbitMq), StringComparison.OrdinalIgnoreCase)
            ? MessagingProvider.RabbitMq
            : MessagingProvider.ServiceBus;
    }

    /// <summary>Registers the outbox transport for the selected provider. Consumers are hosted separately.</summary>
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
