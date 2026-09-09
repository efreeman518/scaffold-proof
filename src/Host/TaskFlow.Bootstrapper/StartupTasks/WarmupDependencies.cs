using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Bootstrapper.StartupTasks;

/// <summary>Configures warmup dependencies host behavior for TaskFlow runtime services.</summary>
public class WarmupDependencies(
    IDbContextFactory<TaskFlowDbContextTrxn> trxnFactory,
    IDbContextFactory<TaskFlowDbContextQuery> queryFactory,
    ILogger<WarmupDependencies> logger) : IStartupTask
{
    /// <summary>Provides the execute operation for warmup dependencies.</summary>
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            await using var trxnDb = await trxnFactory.CreateDbContextAsync(ct);
            await trxnDb.Database.CanConnectAsync(ct);

            await using var queryDb = await queryFactory.CreateDbContextAsync(ct);
            await queryDb.Database.CanConnectAsync(ct);

            logger.DatabaseWarmupCompleted();
        }
        catch (Exception ex)
        {
            logger.DatabaseWarmupFailed(ex);
        }
    }
}
