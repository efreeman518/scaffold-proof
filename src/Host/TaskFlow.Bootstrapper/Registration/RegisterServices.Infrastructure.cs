using EF.Audit.AzureTable;
using EF.AspNetCore.HealthChecks;
using EF.Audit.Contracts;
using EF.Cache;
using EF.CosmosDb;
using EF.Host;
using EF.Messaging.ServiceBus;
using EF.Storage;
using EF.Storage.Contracts;
using EF.Storage.S3;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Caching;
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
        var connection = config.ResolveConnection(
            "TableStorage1",
            "Values:TableStorage1",
            "Aspire:Azure:Data:Tables:TableStorage1:ConnectionString");
        if (string.IsNullOrEmpty(connection))
            throw new InvalidOperationException(
                $"{AuditProviderConfigKey}=AzureTable requires the TableStorage1 endpoint or connection string.");

        services.AddAzureClients(builder =>
        {
            if (ConnectionValue.TryGetServiceUri(connection, out var serviceUri))
            {
                builder.UseCredential(AzureCredentialFactory.Create(config));
                builder.AddTableServiceClient(serviceUri)
                    .WithName("TaskFlowTableClient");
            }
            else
            {
                builder.AddTableServiceClient(connection)
                    .WithName("TaskFlowTableClient");
            }
        });

        // D19: the EF.Audit.AzureTable singleton repository. It never creates the table on the request path;
        // EnsureExternalResources provisions it once through EnsureTableAsync.
        services.AddAzureTableAuditLog(options =>
        {
            options.TableName = "taskflowaudit";
            options.TableServiceClientName = "TaskFlowTableClient";
            config.GetSection(AzureTableAuditLogSettings.ConfigSectionName).Bind(options);
        });
    }

    internal static string? ResolveServiceBusFullyQualifiedNamespace(IConfiguration config) =>
        config["ServiceBus1:fullyQualifiedNamespace"];

    /// <summary>
    /// Registers the strict Azure object store. Missing Blob Storage configuration is a startup error.
    /// </summary>
    private static void AddBlobStorageServices(IServiceCollection services, IConfiguration config)
    {
        var connection = config.ResolveConnection(
            "BlobStorage1",
            "BlobStorage1",
            "BlobStorage1:blobServiceUri",
            "Values:BlobStorage1");
        if (string.IsNullOrEmpty(connection))
            throw new InvalidOperationException(
                $"{StorageProviderConfigKey}=AzureBlob requires the BlobStorage1 endpoint or connection string.");

        services.AddAzureClients(builder =>
        {
            if (ConnectionValue.TryGetServiceUri(connection, out var serviceUri))
            {
                builder.UseCredential(AzureCredentialFactory.Create(config));
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
        var connStr = config.ResolveConnection(
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
                builder.UseCredential(AzureCredentialFactory.Create(config));
                builder.AddServiceBusClientWithNamespace(fullyQualifiedNamespace!)
                    .WithName("TaskFlowSBClient");
            }
        });

        // M14: the package transport packs per destination; an oversize message fails alone as permanent.
        services.AddServiceBusOutboxTransport(o =>
        {
            o.ClientName = "TaskFlowSBClient";
            o.Entities[TaskFlowIntegrationEvents.Destination] = config["DomainEventsTopic"] ?? TaskFlowIntegrationEvents.Destination;
        });
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

        // S20 / D-051: Cosmos:Client binds EF.CosmosDb CosmosClientSettings (PreferredRegions, HedgingEnabled - off by
        // default, it multiplies request units against a slow region - HedgingThresholdMs, HedgingThresholdStepMs).
        var clientOptions = CosmosClientOptionsFactory.Create(
            config.GetSection("Cosmos:Client").Get<CosmosClientSettings>() ?? new CosmosClientSettings());
        services.AddSingleton(_ => ConnectionValue.TryGetServiceUri(connection, out var serviceUri)
            ? new Microsoft.Azure.Cosmos.CosmosClient(
                serviceUri.AbsoluteUri,
                AzureCredentialFactory.Create(config),
                clientOptions)
            : new Microsoft.Azure.Cosmos.CosmosClient(connection, clientOptions));
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
    /// Adds cheap always-on checks first and gates external-service checks behind config so
    /// readiness probes do not require every emulator in lightweight local or test runs.
    /// </summary>
    private static void AddHealthChecks(IServiceCollection services, IConfiguration config)
    {
        var builder = services.AddHealthChecks()
            // Degraded (never Unhealthy) once GC allocated bytes reach MemoryHealthCheckBytesThreshold (default 1 GiB).
            .AddMemoryHealthCheck("memory", tags: ["memory", "full"],
                thresholdInBytes: config.GetValue<long?>("MemoryHealthCheckBytesThreshold"))
            .AddCheck<HealthChecks.SqlHealthCheck>("sql", tags: ["ready", "db", "full"]);

        if (!config.GetValue<bool>("HealthChecks:EnableExternalServices", false))
            return;

        // S19: the checks verify the resource the app uses - the attachment container or bucket (least privilege:
        // container ExistsAsync, a scoped HeadBucket) - and the Cosmos account through the registered client.
        if (!string.IsNullOrWhiteSpace(config.ResolveConnection(
                "BlobStorage1", "BlobStorage1", "BlobStorage1:blobServiceUri", "Values:BlobStorage1")))
            builder.AddBlobContainerHealthCheck(
                "blob-storage", "TaskFlowBlobClient", AttachmentBlobs.ContainerName, tags: ["full", "extservice"]);

        if (ResolveStorageProvider(config) == StorageProvider.S3)
            builder.AddS3BucketHealthCheck("s3-storage", AttachmentBlobs.ContainerName, tags: ["full", "extservice"]);

        if (!string.IsNullOrWhiteSpace(config.ResolveConnection("ServiceBus1", "ServiceBus1", "Values:ServiceBus1"))
            || !string.IsNullOrWhiteSpace(ResolveServiceBusFullyQualifiedNamespace(config)))
            builder.AddServiceBusHealthCheck(
                "TaskFlowSBClient", config["DomainEventsTopic"] ?? TaskFlowIntegrationEvents.Destination, "service-bus", "full", "extservice");

        if (!string.IsNullOrWhiteSpace(config.GetConnectionString("CosmosDb1")))
            builder.AddCosmosDbHealthCheck("cosmos-db", tags: ["full", "extservice"]);

        if (ResolveReadModelProvider(config) == ReadModelProvider.MongoDb)
            builder.AddCheck<HealthChecks.MongoDbHealthCheck>("mongodb", tags: ["full", "extservice"]);

        // S9: a ping over the shared multiplexer, Degraded on failure - the cache falls back to L1 and the rate
        // limiter fails open, so a dead Redis is a problem to page on, not a reason to leave rotation.
        if (services.HasSharedRedis())
            builder.AddRedisHealthCheck("redis-cache", tags: ["full", "extservice"]);
    }
}
