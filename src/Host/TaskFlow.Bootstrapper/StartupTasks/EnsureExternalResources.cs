using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskFlow.Infrastructure.Storage;

namespace TaskFlow.Bootstrapper.StartupTasks;

/// <summary>
/// Creates the external containers and tables the app writes to, once at startup instead of on every write.
/// The per-request <c>CreateIfNotExists</c> calls this replaces cost a round trip on the hot path forever to
/// guard against a condition that can only be true once, and they hid the fact that the app was provisioning
/// its own infrastructure at runtime. Provisioning failures are fatal here for exactly that reason: a host
/// that cannot reach its storage should fail its readiness probe, not fail every request later.
/// </summary>
public sealed class EnsureExternalResources(
    IServiceProvider services,
    IConfiguration config,
    IHostEnvironment environment,
    IOptions<BlobStorageSettings> blobSettings,
    IOptions<AuditLogStorageSettings> auditSettings,
    ILogger<EnsureExternalResources> logger) : IStartupTask
{
    /// <summary>Ensures the attachment container, the audit table, and (in development) the Cosmos view store exist.</summary>
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        await EnsureBlobContainerAsync(ct);
        await EnsureAuditTableAsync(ct);
        await EnsureCosmosAsync(ct);
    }

    /// <summary>Creates the attachment container named by configuration.</summary>
    private async Task EnsureBlobContainerAsync(CancellationToken ct)
    {
        var factory = services.GetService<IAzureClientFactory<BlobServiceClient>>();
        if (factory is null) return;

        var container = factory.CreateClient(blobSettings.Value.BlobServiceClientName)
            .GetBlobContainerClient(blobSettings.Value.ContainerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);

        logger.ExternalResourceReady("blob container", blobSettings.Value.ContainerName);
    }

    /// <summary>Creates the audit table named by configuration.</summary>
    private async Task EnsureAuditTableAsync(CancellationToken ct)
    {
        var factory = services.GetService<IAzureClientFactory<TableServiceClient>>();
        if (factory is null) return;

        var table = factory.CreateClient(auditSettings.Value.TableServiceClientName)
            .GetTableClient(auditSettings.Value.TableName);
        await table.CreateIfNotExistsAsync(ct).ConfigureAwait(false);

        logger.ExternalResourceReady("audit table", auditSettings.Value.TableName);
    }

    /// <summary>
    /// Creates the Cosmos database and container in development only. In a deployed environment Cosmos is
    /// provisioned by Bicep with the throughput, indexing, and backup policy the app has no business choosing,
    /// and the app identity is not expected to hold control-plane rights.
    /// </summary>
    private async Task EnsureCosmosAsync(CancellationToken ct)
    {
        if (!environment.IsDevelopment()) return;

        var client = services.GetService<CosmosClient>();
        if (client is null) return;

        var databaseName = config["Cosmos:TaskViews:DatabaseName"] ?? "taskflow-db";
        var containerName = config["Cosmos:TaskViews:ContainerName"] ?? "task-views";

        var database = await client.CreateDatabaseIfNotExistsAsync(databaseName, cancellationToken: ct)
            .ConfigureAwait(false);
        // Partitioned by tenant: every read of the view store is already tenant-scoped, so this is the
        // partition key that keeps a query inside one partition.
        await database.Database
            .CreateContainerIfNotExistsAsync(new ContainerProperties(containerName, "/tenantId"), cancellationToken: ct)
            .ConfigureAwait(false);

        logger.ExternalResourceReady("cosmos container", $"{databaseName}/{containerName}");
    }
}
