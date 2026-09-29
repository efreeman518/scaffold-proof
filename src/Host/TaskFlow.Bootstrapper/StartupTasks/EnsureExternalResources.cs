using EF.Audit.AzureTable;
using EF.Common.Contracts;
using Azure.Storage.Blobs;
using EF.Storage.S3;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Repositories.MongoDb;
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
    IDistributedLock distributedLock,
    ILogger<EnsureExternalResources> logger) : IStartupTask
{
    /// <summary>Lock key every replica of every host competes for (D-052).</summary>
    private const string ProvisionLockKey = "taskflow:provision";

    /// <summary>
    /// Longer than provisioning takes, short enough that a crashed holder does not block a deploy. A holder
    /// that overran would lose the lock while still working, which is why this is not tighter.
    /// </summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(60);

    /// <summary>How long a losing replica waits for the lock before provisioning without it.</summary>
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Ensures the attachment container or S3 bucket, the audit table, and (in development) the Cosmos view
    /// store exist.
    /// <para>
    /// D-052: the lock serializes provisioning; it does not deduplicate it. <c>CreateIfNotExists</c> is idempotent
    /// but not serialized across processes, and Cosmos in particular answers a concurrent create with a conflict
    /// rather than a no-op - which would make this fatal startup task fail on the replica that lost the race.
    /// So every replica provisions, one at a time: a free lock only means the previous holder stopped, not that
    /// it succeeded (it may have thrown, or died and let the TTL expire), and a replica that skipped the work on
    /// that signal would report ready with no container or table. The re-run costs one round trip per resource.
    /// </para>
    /// <para>
    /// A wait that outlasts <see cref="WaitBudget"/> provisions without the lock and logs a warning: a slow or
    /// stuck peer must not keep this replica from ever confirming its resources exist.
    /// </para>
    /// </summary>
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        // D5: an immediate attempt, then polls until the budget is spent; null means the budget ran out.
        var lease = await distributedLock.AcquireWithinAsync(ProvisionLockKey, LockTtl, WaitBudget, ct: ct)
            .ConfigureAwait(false);
        if (lease is null)
        {
            logger.ProvisioningWaitTimedOut(ProvisionLockKey, (int)WaitBudget.TotalSeconds);
            await ProvisionAsync(ct).ConfigureAwait(false);
            return;
        }

        await using (lease.ConfigureAwait(false))
        {
            logger.ProvisioningAcquired(ProvisionLockKey);
            await ProvisionAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs every idempotent ensure step; each is a no-op when its provider is not configured.</summary>
    private async Task ProvisionAsync(CancellationToken ct)
    {
        await EnsureBlobContainerAsync(ct);
        await EnsureS3BucketAsync(ct);
        await EnsureAuditTableAsync(ct);
        await EnsureCosmosAsync(ct);
        await EnsureMongoDbAsync(ct);
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

    /// <summary>Creates the attachment bucket when the S3 object-storage arm is active (D-037).</summary>
    private async Task EnsureS3BucketAsync(CancellationToken ct)
    {
        var provisioner = services.GetService<IS3BucketProvisioner>();
        if (provisioner is null) return;

        await provisioner.EnsureBucketExistsAsync(AttachmentBlobs.ContainerName, ct).ConfigureAwait(false);

        logger.ExternalResourceReady("s3 bucket", AttachmentBlobs.ContainerName);
    }

    /// <summary>Creates the audit table when the Azure Table audit arm is active (D19).</summary>
    private async Task EnsureAuditTableAsync(CancellationToken ct)
    {
        var auditLog = services.GetService<AzureTableAuditLogRepository>();
        if (auditLog is null) return;

        await auditLog.EnsureTableAsync(ct).ConfigureAwait(false);

        logger.ExternalResourceReady("audit table",
            services.GetRequiredService<IOptions<AzureTableAuditLogSettings>>().Value.TableName);
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

    /// <summary>Creates MongoDB TaskView indexes once under the same cross-replica provisioning lock.</summary>
    private async Task EnsureMongoDbAsync(CancellationToken ct)
    {
        var repository = services.GetService<MongoTaskViewRepository>();
        if (repository is null) return;

        await repository.EnsureIndexesAsync(ct).ConfigureAwait(false);
        var settings = services.GetRequiredService<MongoTaskViewSettings>();
        logger.ExternalResourceReady("mongodb collection", $"{settings.DatabaseName}/{settings.CollectionName}");
    }
}
