using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Bootstrapper.HealthChecks;

/// <summary>
/// Reports the transactional database. <c>CanConnectAsync</c> answers an unreachable database with false rather
/// than an exception, so its result is the verdict; the catch covers failures building the context or opening the
/// connection. Both report the registration's failure status.
/// </summary>
public class SqlHealthCheck(IDbContextFactory<TaskFlowDbContextTrxn> factory) : IHealthCheck
{
    /// <summary>Provides the check health operation for SQL health check.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var db = await factory.CreateDbContextAsync(ct);
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "SQL connection failed");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "SQL connection failed", ex);
        }
    }
}
