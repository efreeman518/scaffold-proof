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

    /// <summary>A PostgreSQL JSONB TaskView projection.</summary>
    PostgreSqlJsonb,

    /// <summary>A MongoDB TaskView projection.</summary>
    MongoDb
}

public static partial class RegisterServices
{
    public const string ReadModelProviderConfigKey = HostingLaneResolver.ReadModelConfigurationKey;
    public const string ReadModelProviderEnvVar = HostingLaneResolver.ReadModelEnvironmentVariable;

    /// <summary>
    /// Resolves the strict lane's read-model backend through the shared D-060 contract. Relational input is
    /// normalized to PostgreSqlJsonb for one release by that contract.
    /// </summary>
    public static ReadModelProvider ResolveReadModelProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ParseReadModelProvider(HostingLaneResolver.Resolve(config).ReadModel);
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
            case ReadModelProvider.PostgreSqlJsonb:
                AddRelationalReadModelServices(services);
                break;
            case ReadModelProvider.MongoDb:
                throw new NotSupportedException(
                    $"{ReadModelProviderConfigKey}=MongoDb requires the MongoDB repository adapter.");
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
