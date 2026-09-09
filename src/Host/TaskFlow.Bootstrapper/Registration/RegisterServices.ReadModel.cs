using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Repositories;

namespace TaskFlow.Bootstrapper;

/// <summary>Read-model backend selected for this deployment (D-038).</summary>
public enum ReadModelProvider
{
    /// <summary>The existing denormalized Cosmos DB TaskView projection.</summary>
    Cosmos,

    /// <summary>A relational TaskView table in the application database.</summary>
    Relational
}

public static partial class RegisterServices
{
    public const string ReadModelProviderConfigKey = "ReadModel:Provider";
    public const string ReadModelProviderEnvVar = "TASKFLOW_READMODEL_PROVIDER";

    /// <summary>
    /// Resolves the read-model backend. The environment variable wins over configuration; when neither is
    /// set, the Portable lane defaults to Relational and the Azure lane keeps today's Cosmos default (D-035).
    /// </summary>
    public static ReadModelProvider ResolveReadModelProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var value = Environment.GetEnvironmentVariable(ReadModelProviderEnvVar) ?? config[ReadModelProviderConfigKey];
        if (!string.IsNullOrWhiteSpace(value)) return ParseReadModelProvider(value);

        return HostingLaneSelector.Resolve(config) == HostingLane.Portable
            ? ReadModelProvider.Relational
            : ReadModelProvider.Cosmos;
    }

    private static ReadModelProvider ParseReadModelProvider(string value) =>
        Enum.TryParse<ReadModelProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown read-model provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<ReadModelProvider>())}.");

    /// <summary>Dispatches to the selected read-model backend.</summary>
    [ProviderSwitch(typeof(ITaskViewRepository))]
    private static void AddReadModelServices(IServiceCollection services, IConfiguration config)
    {
        switch (ResolveReadModelProvider(config))
        {
            case ReadModelProvider.Cosmos:
                AddCosmosDbServices(services, config);
                break;
            case ReadModelProvider.Relational:
                AddRelationalReadModelServices(services);
                break;
        }
    }

    /// <summary>
    /// Relational TaskView read model (D-038). Scoped over the pooled context factories already registered
    /// by <see cref="AddDatabaseServices"/>, so the read model shares the application database rather than
    /// adding one. No connection-string gate and no extra health check: the always-on <c>sql</c> readiness
    /// check already covers this store, and a missing database connection is a startup failure for the whole
    /// host, not a degradation of this one repository.
    /// </summary>
    private static void AddRelationalReadModelServices(IServiceCollection services) =>
        services.AddScoped<ITaskViewRepository, RelationalTaskViewRepository>();
}
