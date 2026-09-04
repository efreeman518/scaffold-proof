using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace TaskFlow.Infrastructure.Data;

/// <summary>
/// Verifies TickerQ's operational tables exist without creating or changing schema.
/// Provider-neutral: INFORMATION_SCHEMA.TABLES is available verbatim on SQL Server and PostgreSQL.
/// </summary>
public static class TaskFlowTickerQSchemaValidator
{
    private static readonly string[] RequiredTables = ["TimeTickers", "CronTickers", "CronTickerOccurrences"];

    public static async Task<bool> SchemaExistsAsync(
        TaskFlowTickerQDbContext db,
        CancellationToken cancellationToken = default)
    {
        // Startup validation checks only the contract Scheduler needs to run.
        // EF migrations and history-table state remain the migrator's responsibility.
        if (!await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(cancellationToken))
        {
            return false;
        }

        var schema = TaskFlowTickerQDbContext.SchemaName;
        var count = await db.Database
            .SqlQuery<int>($"""
                SELECT CAST(COUNT(*) AS int) AS "Value"
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = {schema}
                  AND TABLE_NAME IN ({RequiredTables[0]}, {RequiredTables[1]}, {RequiredTables[2]})
                """)
            .SingleAsync(cancellationToken);

        return count == RequiredTables.Length;
    }
}
