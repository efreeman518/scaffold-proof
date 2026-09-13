using EF.Audit.Contracts;
using EF.Storage.Contracts;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Repositories.MongoDb;
using TaskFlow.Infrastructure.Storage;
using TaskFlow.Infrastructure.Storage.CosmosDb;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures register services host behavior for TaskFlow runtime services.</summary>
public static partial class RegisterServices
{
    /// <summary>
    /// Registers the strict Azure audit sink. Missing Table Storage configuration is a startup error.
    /// </summary>
    private static void AddTableStorageServices(IServiceCollection services, IConfiguration config)
    {
        var connection = ResolveConnectionString(
            config,
            "TableStorage1",
            "Values:TableStorage1",
            "Aspire:Azure:Data:Tables:TableStorage1:ConnectionString");
        if (string.IsNullOrEmpty(connection))
            throw new InvalidOperationException(
                $"{AuditProviderConfigKey}=AzureTable requires the TableStorage1 endpoint or connection string.");

        services.AddAzureClients(builder =>
        {
            if (TryGetServiceUri(connection, out var serviceUri))
            {
                builder.UseCredential(CreateAzureCredential(config));
                builder.AddTableServiceClient(serviceUri)
                    .WithName("TaskFlowTableClient");
            }
            else
            {
                builder.AddTableServiceClient(connection)
                    .WithName("TaskFlowTableClient");
            }
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

    private static bool TryGetServiceUri(string value, out Uri serviceUri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
        {
            serviceUri = candidate;
            return true;
        }

        serviceUri = null!;
        return false;
    }

    internal static string? ResolveServiceBusFullyQualifiedNamespace(IConfiguration config) =>
        config["ServiceBus1:fullyQualifiedNamespace"];

    /// <summary>
    /// Registers the strict Azure object store. Missing Blob Storage configuration is a startup error.
    /// </summary>
    private static void AddBlobStorageServices(IServiceCollection services, IConfiguration config)
    {
        var connection = ResolveConnectionString(
            config,
            "BlobStorage1",
            "BlobStorage1",
            "BlobStorage1:blobServiceUri",
            "Values:BlobStorage1");
        if (string.IsNullOrEmpty(connection))
            throw new InvalidOperationException(
                $"{StorageProviderConfigKey}=AzureBlob requires the BlobStorage1 endpoint or connection string.");

        services.AddAzureClients(builder =>
        {
            if (TryGetServiceUri(connection, out var serviceUri))
            {
                builder.UseCredential(CreateAzureCredential(config));
                builder.AddBlobServiceClient(serviceUri)
                    .WithName("TaskFlowBlobClient");
            }
            else
            {
                builder.AddBlobServiceClient(connection)
                    .WithName("TaskFlowBlobClient");
            }
        });

        services.Configure<BlobStorageSettings>(
            config.GetSection("BlobStorageSettings"));

        services.AddScoped<IObjectStorageRepository, BlobStorageRepository>();
    }

    /// <summary>
    /// Registers the strict Azure Service Bus transport. Missing broker configuration is a startup error.
    /// </summary>
    private static void AddServiceBusServices(IServiceCollection services, IConfiguration config)
    {
        var connStr = ResolveConnectionString(
            config,
            "ServiceBus1",
            "ServiceBus1",
            "Values:ServiceBus1");
        var fullyQualifiedNamespace = ResolveServiceBusFullyQualifiedNamespace(config);
        if (string.IsNullOrEmpty(connStr) && string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
            throw new InvalidOperationException(
                $"{MessagingProviderConfigKey}=ServiceBus requires the ServiceBus1 namespace or connection string.");

        services.AddAzureClients(builder =>
        {
            if (!string.IsNullOrEmpty(connStr))
            {
                builder.AddServiceBusClient(connStr)
                    .WithName("TaskFlowSBClient");
            }
            else
            {
                builder.UseCredential(CreateAzureCredential(config));
                builder.AddServiceBusClientWithNamespace(fullyQualifiedNamespace!)
                    .WithName("TaskFlowSBClient");
            }
        });

        services.AddSingleton<IIntegrationEventTransport, ServiceBusEventTransport>();
    }

    /// <summary>
    /// Registers the strict Azure Cosmos DB read model. Missing Cosmos configuration is a startup error.
    /// </summary>
    private static void AddCosmosDbServices(IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("CosmosDb1");
        if (string.IsNullOrEmpty(connection))
        {
            if (config.GetValue("Testing:UseNoOpCosmosReadModel", false)
                && IsTestingHost(config))
            {
                services.AddSingleton<ITaskViewRepository, NoOpTaskViewRepository>();
                return;
            }

            throw new InvalidOperationException(
                $"{ReadModelProviderConfigKey}=Cosmos requires the CosmosDb1 endpoint or connection string.");
        }

        var databaseName = config["Cosmos:TaskViews:DatabaseName"] ?? "taskflow-db";
        var containerName = config["Cosmos:TaskViews:ContainerName"] ?? "task-views";

        services.AddSingleton(_ => TryGetServiceUri(connection, out var serviceUri)
            ? new Microsoft.Azure.Cosmos.CosmosClient(
                serviceUri.AbsoluteUri,
                CreateAzureCredential(config),
                BuildCosmosClientOptions(config))
            : new Microsoft.Azure.Cosmos.CosmosClient(connection, BuildCosmosClientOptions(config)));
        services.AddSingleton<ITaskViewRepository>(sp =>
            new CosmosTaskViewRepository(
                sp.GetRequiredService<Microsoft.Azure.Cosmos.CosmosClient>(),
                sp.GetRequiredService<ILogger<CosmosTaskViewRepository>>(),
                databaseName,
                containerName));
    }

    private static bool IsTestingHost(IConfiguration config)
    {
        var dotnetEnvironment = config["DOTNET_ENVIRONMENT"];
        var aspNetCoreEnvironment = config["ASPNETCORE_ENVIRONMENT"];
        var oneIsTesting = string.Equals(dotnetEnvironment, "Testing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(aspNetCoreEnvironment, "Testing", StringComparison.OrdinalIgnoreCase);
        var neitherConflicts = (string.IsNullOrWhiteSpace(dotnetEnvironment)
                || string.Equals(dotnetEnvironment, "Testing", StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(aspNetCoreEnvironment)
                || string.Equals(aspNetCoreEnvironment, "Testing", StringComparison.OrdinalIgnoreCase));
        return oneIsTesting && neitherConflicts;
    }

    /// <summary>
    /// D-051: cross-region read hedging, off by default. After <c>threshold</c> without an answer the SDK
    /// issues the same read against the next preferred region and takes whichever replies first, then repeats
    /// every <c>thresholdStep</c>. It only helps a multi-region account with preferred regions configured, and
    /// it multiplies request units on a slow region, so it stays a deployment decision rather than a default.
    /// </summary>
    internal static Microsoft.Azure.Cosmos.CosmosClientOptions? BuildCosmosClientOptions(IConfiguration config)
    {
        if (!config.GetValue("Cosmos:Hedging:Enabled", false))
            return null;

        var threshold = TimeSpan.FromMilliseconds(config.GetValue("Cosmos:Hedging:ThresholdMs", 500));
        var thresholdStep = TimeSpan.FromMilliseconds(config.GetValue("Cosmos:Hedging:ThresholdStepMs", 100));

        return new Microsoft.Azure.Cosmos.CosmosClientOptions
        {
            AvailabilityStrategy =
                Microsoft.Azure.Cosmos.AvailabilityStrategy.CrossRegionHedgingStrategy(threshold, thresholdStep)
        };
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

        if (!string.IsNullOrWhiteSpace(ResolveConnectionString(
                config, "BlobStorage1", "BlobStorage1", "BlobStorage1:blobServiceUri", "Values:BlobStorage1")))
            builder.AddCheck<HealthChecks.BlobStorageHealthCheck>("blob-storage", tags: ["full", "extservice"]);

        if (ResolveStorageProvider(config) == StorageProvider.S3)
            builder.AddCheck<HealthChecks.S3StorageHealthCheck>("s3-storage", tags: ["full", "extservice"]);

        if (!string.IsNullOrWhiteSpace(ResolveConnectionString(config, "ServiceBus1", "ServiceBus1", "Values:ServiceBus1"))
            || !string.IsNullOrWhiteSpace(ResolveServiceBusFullyQualifiedNamespace(config)))
            builder.AddCheck<HealthChecks.ServiceBusHealthCheck>("service-bus", tags: ["full", "extservice"]);

        if (!string.IsNullOrWhiteSpace(config.GetConnectionString("CosmosDb1")))
            builder.AddCheck<HealthChecks.CosmosDbHealthCheck>("cosmos-db", tags: ["full", "extservice"]);

        if (ResolveReadModelProvider(config) == ReadModelProvider.MongoDb)
            builder.AddCheck<HealthChecks.MongoDbHealthCheck>("mongodb", tags: ["full", "extservice"]);

        if (!string.IsNullOrWhiteSpace(config.GetConnectionString("Redis1")))
            builder.AddCheck<HealthChecks.RedisCacheHealthCheck>("redis-cache", tags: ["full", "extservice"]);
    }
}
