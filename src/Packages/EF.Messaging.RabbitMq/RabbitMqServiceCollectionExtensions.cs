using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EF.Messaging.RabbitMq;

/// <summary>Registration entry points for the RabbitMQ transport.</summary>
public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="RabbitMqOptions"/> from configuration with startup validation and registers the
    /// multiplexer, publisher, topology declarer and metrics as singletons. Safe to call more than once.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration root or section parent.</param>
    /// <param name="sectionName">Configuration path to bind, colon separated.</param>
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, IConfiguration configuration, string sectionName = "Messaging:RabbitMq")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(sectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>(CreateOptionsValidator));

        services.TryAddSingleton<RabbitMqMetrics>();
        services.TryAddSingleton<IRabbitMqConnectionMultiplexer, RabbitMqConnectionMultiplexer>();
        services.TryAddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();
        services.TryAddSingleton<IRabbitMqTopologyDeclarer, RabbitMqTopologyDeclarer>();

        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="THandler"/> as scoped and a hosted service consuming <paramref name="queue"/>.
    /// The hosted service starts no consumer when <c>Consumers[queue].Enabled</c> is false.
    /// </summary>
    /// <typeparam name="THandler">Handler invoked for every delivery.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="queue">Queue name; also the key into <see cref="RabbitMqOptions.Consumers"/>.</param>
    public static IServiceCollection AddRabbitMqConsumer<THandler>(this IServiceCollection services, string queue)
        where THandler : class, IRabbitMqMessageHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);

        services.TryAddScoped<THandler>();
        services.AddSingleton<IHostedService>(sp => new RabbitMqConsumerHostedService<THandler>(
            queue,
            sp.GetRequiredService<IRabbitMqConnectionMultiplexer>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptionsMonitor<RabbitMqOptions>>(),
            sp.GetRequiredService<RabbitMqMetrics>(),
            sp.GetRequiredService<ILogger<RabbitMqConsumerHostedService<THandler>>>()));

        return services;
    }

    /// <summary>
    /// Declares <paramref name="topology"/> once at startup. The hosted service is inserted at the front of the
    /// collection so the topology exists before any consumer subscribes, whatever order the app registers in.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="topology">Exchanges, queues and bindings to declare.</param>
    public static IServiceCollection AddRabbitMqTopology(this IServiceCollection services, RabbitMqTopology topology)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(topology);

        services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(sp => new RabbitMqTopologyStartup(
            topology,
            sp.GetRequiredService<IRabbitMqTopologyDeclarer>())));

        return services;
    }

    /// <summary>Adds a health check that reports on the transport's connection.</summary>
    /// <param name="builder">Health checks builder.</param>
    /// <param name="name">Health check name.</param>
    /// <param name="tags">Tags used to filter health check endpoints.</param>
    public static IHealthChecksBuilder AddRabbitMqHealthCheck(this IHealthChecksBuilder builder, string name = "rabbitmq", params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<RabbitMqHealthCheck>(name, HealthStatus.Unhealthy, tags);
    }

    private static RabbitMqOptionsValidator CreateOptionsValidator(IServiceProvider sp)
    {
        // IServiceProviderIsService answers the "is an IConnection registered" question without building one,
        // so validation never opens a connection.
        IServiceProviderIsService? probe = sp.GetService<IServiceProviderIsService>();
        return new RabbitMqOptionsValidator(() => probe is not null
            ? probe.IsService(typeof(IConnection))
            : sp.GetService<IConnection>() is not null);
    }
}
