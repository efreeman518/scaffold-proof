using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>Builds a container or a host wired to the shared broker with the transport registered.</summary>
internal static class TestServices
{
    /// <summary>Configuration for the transport pointed at <paramref name="broker"/>.</summary>
    internal static IConfiguration Configuration(RabbitMqContainer broker, string clientName, IEnumerable<KeyValuePair<string, string?>>? extra = null)
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["Messaging:RabbitMq:ConnectionString"] = broker.GetConnectionString(),
            ["Messaging:RabbitMq:ClientProvidedName"] = clientName
        };

        foreach ((string key, string? value) in extra ?? [])
            settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>A provider with the transport registered and nothing else; no hosted services run.</summary>
    internal static ServiceProvider Build(RabbitMqContainer broker, string clientName, IEnumerable<KeyValuePair<string, string?>>? extra = null, Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddRabbitMqMessaging(Configuration(broker, clientName, extra));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>A started host, so topology declaration and consumer hosted services run as they do in an app.</summary>
    internal static async Task<IHost> StartHostAsync(
        RabbitMqContainer broker,
        string clientName,
        IEnumerable<KeyValuePair<string, string?>> settings,
        Action<IServiceCollection> configure,
        CancellationToken ct)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddConfiguration(Configuration(broker, clientName, settings));
        builder.Services.AddLogging();
        builder.Services.AddRabbitMqMessaging(builder.Configuration);
        configure(builder.Services);

        IHost host = builder.Build();
        await host.StartAsync(ct);
        return host;
    }
}
