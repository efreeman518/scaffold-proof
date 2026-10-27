using EF.Data.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace TaskFlow.Infrastructure.Data;

/// <summary>
/// The EF.Data.Scheduling tables (job queue, cron state, run history, job catalog) in their own schema and migration
/// history. The migrator applies its migrations; the Scheduler runs on it and validates the schema at start, never
/// creating or patching it.
/// </summary>
public sealed class TaskFlowSchedulerDbContext(DbContextOptions<TaskFlowSchedulerDbContext> options) : DbContext(options)
{
    // Lower-case so the identifier round-trips unquoted on PostgreSQL (folds to lower case) and SQL Server alike.
    public const string SchemaName = "scheduler";
    public const string MigrationHistoryTable = "__EFMigrationsHistory_Scheduler";

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplySchedulingModel(SchemaName);
}
