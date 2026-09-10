using Azure.Data.Tables;
using Azure.Storage.Blobs;
using EF.Storage.S3;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Locking;
using TaskFlow.Application.Contracts.Storage;
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

    /// <summary>How long a losing replica waits for the winner to finish before giving up on confirmation.</summary>
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ensures the attachment container or S3 bucket, the audit table, and (in development) the Cosmos view
    /// store exist.
    /// <para>
    /// D-052: one replica provisions, the rest wait for it. <c>CreateIfNotExists</c> is idempotent but not
    /// serialized across processes, and Cosmos in particular answers a concurrent create with a conflict
    /// rather than a no-op - which would make this fatal startup task fail on the replica that lost the race.
    /// The losing replicas wait rather than continuing immediately so this host does not report ready before
    /// the resources it needs exist.
    /// </para>
    /// </summary>
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var lease = await distributedLock.TryAcquireAsync(ProvisionLockKey, LockTtl, ct).ConfigureAwait(false);
        if (lease is null)
        {
            logger.ProvisioningDeferred(ProvisionLockKey);
            await WaitForProvisioningAsync(ct).ConfigureAwait(false);
            return;
        }

        await using (lease.ConfigureAwait(false))
        {
            logger.ProvisioningAcquired(ProvisionLockKey);
            await EnsureBlobContainerAsync(ct);
            await EnsureS3BucketAsync(ct);
            await EnsureAuditTableAsync(ct);
            await EnsureCosmosAsync(ct);
        }
    }

    /// <summary>
    /// Polls the lock until it is free, which is the signal that the holder finished, then releases it again
    /// and skips provisioning - the work is already done. A timeout is logged as a warning rather than thrown:
    /// the resources may well exist, and failing startup here would take down a replica for a slow peer.
    /// </summary>
    private async Task WaitForProvisioningAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + WaitBudget;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);

            var lease = await distributedLock.TryAcquireAsync(ProvisionLockKey, LockTtl, ct).ConfigureAwait(false);
            if (lease is null) continue;

            await lease.DisposeAsync().ConfigureAwait(false);
            logger.ProvisioningSkipped(ProvisionLockKey);
            return;
        }

        logger.ProvisioningWaitTimedOut(ProvisionLockKey, (int)WaitBudget.TotalSeconds);
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
