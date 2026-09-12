using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Infrastructure.Repositories.MongoDb;

namespace TaskFlow.Bootstrapper.HealthChecks;

/// <summary>Checks the configured MongoDB TaskView database with the driver's ping command.</summary>
public sealed class MongoDbHealthCheck(MongoTaskViewRepository repository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await repository.CheckConnectivityAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB read-model connection failed.", ex);
        }
    }
}
