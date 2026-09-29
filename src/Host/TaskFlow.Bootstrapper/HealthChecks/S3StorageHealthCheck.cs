using EF.Storage.S3;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Bootstrapper.HealthChecks;

/// <summary>Configures S3 object-storage health check host behavior for TaskFlow runtime services (D-037).</summary>
public sealed class S3StorageHealthCheck(IS3BucketProvisioner provisioner) : IHealthCheck
{
    /// <summary>Provides the check health operation for the S3 object-storage health check.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // A scoped HeadBucket on the attachment bucket EnsureExternalResources provisions at startup.
            await provisioner.CheckBucketAsync(AttachmentBlobs.ContainerName, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("S3 object-storage connection failed.", ex);
        }
    }
}
