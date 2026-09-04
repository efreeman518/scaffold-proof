using Microsoft.EntityFrameworkCore;
using TickerQ.EntityFrameworkCore.DbContextFactory;
using TickerQ.Utilities.Entities;

namespace TaskFlow.Infrastructure.Data;

/// <summary>
/// EF tooling and migrator context for TickerQ operational tables.
/// Scheduler runtime uses the same model but never creates or patches this schema at startup.
/// </summary>
public sealed class TaskFlowTickerQDbContext(DbContextOptions<TaskFlowTickerQDbContext> options)
    : TickerQDbContext<TimeTickerEntity, CronTickerEntity>(options)
{
    // Lower-case so the identifier round-trips unquoted on PostgreSQL (folds to lower case) and SQL Server alike.
    public const string SchemaName = "scheduler";
    public const string MigrationHistoryTable = "__EFMigrationsHistory_TickerQ";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<TimeTickerEntity>().ToTable("TimeTickers", SchemaName);
        modelBuilder.Entity<CronTickerEntity>().ToTable("CronTickers", SchemaName);
        modelBuilder.Entity<CronTickerOccurrenceEntity<CronTickerEntity>>()
            .ToTable("CronTickerOccurrences", SchemaName);
    }
}
