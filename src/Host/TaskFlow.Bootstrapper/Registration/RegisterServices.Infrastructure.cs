using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Storage;
using TaskFlow.Infrastructure.Storage.CosmosDb;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures register services host behavior for TaskFlow runtime services.</summary>
public static partial class RegisterServices
{
    /// <summary>
    /// Registers audit persistence when a Table Storage connection exists; otherwise keeps
    /// audit message handling alive with a no-op repository for local and test hosts.
    /// </summary>
    private static void AddTableStorageServices(IServiceCollection services, IConfiguration config)
    {
        var connStr = ResolveConnectionString(
            config,
            "TableStorage1",
            "Values:TableStorage1",
            "Aspire:Azure:Data:Tables:TableStorage1:ConnectionString");
        if (string.IsNullOrEmpty(connStr))
        {
            services.AddSingleton<IAuditLogRepository, NoOpAuditLogRepository>();
            return;
        }

        services.AddAzureClients(builder =>
        {
            builder.AddTableServiceClient(connStr)
                .WithName("TaskFlowTableClient");
        });

        services.Configure<AuditLogStorageSettings>(
            config.GetSection(AuditLogStorageSettings.ConfigSectionName));

        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
    }

    /// <summary>
    /// Resolves Aspire, appsettings, and Functions-style connection keys in a stable order.
    /// A real connection string wins over UseDevelopmentStorage=true, but the emulator value
    /// is kept as a fallback when it is the only configured value.
    /// </summary>
    private static string? ResolveConnectionString(IConfiguration config, string connectionName, params string[] alternateKeys)
    {
        string? fallbackConnectionString = null;

        foreach (var candidate in GetConnectionStringCandidates(config, connectionName, alternateKeys))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            fallbackConnectionString ??= candidate;

            if (!string.Equals(candidate, "UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return fallbackConnectionString;
    }

    /// <summary>
    /// Enumerates connection-string sources without assuming which host supplied them.
    /// ASP.NET, Aspire, and Azure Functions use different key shapes for the same resource.
    /// </summary>
    private static IEnumerable<string?> GetConnectionStringCandidates(IConfiguration config, string connectionName, IEnumerable<string> alternateKeys)
    {
        yield return Environment.GetEnvironmentVariable($"ConnectionStrings__{connectionName}");
        yield return config.GetConnectionString(connectionName);

        foreach (var key in alternateKeys)
        {
            yield return Environment.GetEnvironmentVariable(key.Replace(":", "__"));
            yield return config[key];
        }
    }

    /// <summary>
    /// Registers attachment blob storage when configured; otherwise a no-op repository keeps
    /// <see cref="IBlobStorageRepository"/> resolvable so the DI graph still builds (D-037). Upload/download
    /// endpoints surface a service-level failure from the no-op when this optional dependency is absent.
    /// </summary>
    private static void AddBlobStorageServices(IServiceCollection services, IConfiguration config)
    {
        var connStr = ResolveConnectionString(
            config,
            "BlobStorage1",
            "BlobStorage1",
            "Values:BlobStorage1");
        if (string.IsNullOrEmpty(connStr))
        {
            services.AddSingleton<IBlobStorageRepository, NoOpBlobStorageRepository>();
            return;
        }

        services.AddAzureClients(builder =>
        {
            builder.AddBlobServiceClient(connStr)
                .WithName("TaskFlowBlobClient");
        });

        services.Configure<BlobStorageSettings>(
            config.GetSection("BlobStorageSettings"));

        services.AddScoped<IBlobStorageRepository, BlobStorageRepository>();
    }

    /// <summary>
    /// Registers the Service Bus outbox transport when a namespace is configured; otherwise a transport that
    /// reports it cannot dispatch, so staged rows stay in the outbox instead of being dropped (D-026).
    /// </summary>
    private static void AddServiceBusServices(IServiceCollection services, IConfiguration config)
    {
        var connStr = ResolveConnectionString(
            config,
            "ServiceBus1",
            "ServiceBus1",
            "Values:ServiceBus1");
        if (string.IsNullOrEmpty(connStr))
        {
            services.AddSingleton<IIntegrationEventTransport, NoOpEventTransport>();
            return;
        }

        services.AddAzureClients(builder =>
        {
            builder.AddServiceBusClient(connStr)
                .WithName("TaskFlowSBClient");
        });

        services.AddSingleton<IIntegrationEventTransport, ServiceBusEventTransport>();
    }

    /// <summary>
    /// Registers the denormalized TaskView read model in Cosmos DB when configured.
    /// The no-op repository preserves API shape when the Cosmos emulator is intentionally skipped.
    /// </summary>
    private static void AddCosmosDbServices(IServiceCollection services, IConfiguration config)
    {
        var connStr = config.GetConnectionString("CosmosDb1");
        if (string.IsNullOrEmpty(connStr))
        {
            services.AddSingleton<ITaskViewRepository, NoOpTaskViewRepository>();
            return;
        }

        var databaseName = config["Cosmos:TaskViews:DatabaseName"] ?? "taskflow-db";
        var containerName = config["Cosmos:TaskViews:ContainerName"] ?? "task-views";

        services.AddSingleton(_ => new Microsoft.Azure.Cosmos.CosmosClient(connStr));
        services.AddSingleton<ITaskViewRepository>(sp =>
            new CosmosTaskViewRepository(
                sp.GetRequiredService<Microsoft.Azure.Cosmos.CosmosClient>(),
                sp.GetRequiredService<ILogger<CosmosTaskViewRepository>>(),
                databaseName,
                containerName));
    }

    /// <summary>
    /// Adds cheap always-on checks first and gates external-service checks behind config so
    /// readiness probes do not require every emulator in lightweight local or test runs.
    /// </summary>
    private static void AddHealthChecks(IServiceCollection services, IConfiguration config)
    {
        var builder = services.AddHealthChecks()
            .AddCheck<HealthChecks.MemoryHealthCheck>("memory", tags: ["memory", "full"])
            .AddCheck<HealthChecks.SqlHealthCheck>("sql", tags: ["ready", "db", "full"]);

        if (!config.GetValue<bool>("HealthChecks:EnableExternalServices", false))
            return;

        if (!string.IsNullOrWhiteSpace(ResolveConnectionString(config, "BlobStorage1", "BlobStorage1", "Values:BlobStorage1")))
            builder.AddCheck<HealthChecks.BlobStorageHealthCheck>("blob-storage", tags: ["full", "extservice"]);

        if (ResolveStorageProvider(config) == StorageProvider.S3)
            builder.AddCheck<HealthChecks.S3StorageHealthCheck>("s3-storage", tags: ["full", "extservice"]);

        if (!string.IsNullOrWhiteSpace(ResolveConnectionString(config, "ServiceBus1", "ServiceBus1", "Values:ServiceBus1")))
            builder.AddCheck<HealthChecks.ServiceBusHealthCheck>("service-bus", tags: ["full", "extservice"]);

        if (!string.IsNullOrWhiteSpace(config.GetConnectionString("CosmosDb1")))
            builder.AddCheck<HealthChecks.CosmosDbHealthCheck>("cosmos-db", tags: ["full", "extservice"]);

        if (!string.IsNullOrWhiteSpace(config.GetConnectionString("Redis1")))
            builder.AddCheck<HealthChecks.RedisCacheHealthCheck>("redis-cache", tags: ["full", "extservice"]);
    }
}
