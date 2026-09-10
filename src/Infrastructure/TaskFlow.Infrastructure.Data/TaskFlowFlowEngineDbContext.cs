using EF.FlowEngine.CircuitBreaker.Sql;
using EF.FlowEngine.Model;
using EF.FlowEngine.Outbox.Sql;
using EF.FlowEngine.Sql;
using Microsoft.EntityFrameworkCore;

namespace TaskFlow.Infrastructure.Data;

// FlowEngine SQL composition pattern: this context inherits the standard DbContext and
// opts into state + outbox + circuit-breaker by implementing the matching interfaces and
// applying the mixin extensions in OnModelCreating. The same SaveChangesAsync persists
// workflow execution rows alongside the outbox staging rows, preserving the atomic
// save+enqueue guarantee of SqlOutboxStore.
//
// Co-located with TaskFlowDbContextTrxn on the same SQL Server connection but in its own
// `flowengine` schema with an isolated migration history table so it does not collide with
// the application's own migrations.
public sealed class TaskFlowFlowEngineDbContext(DbContextOptions<TaskFlowFlowEngineDbContext> options)
    : DbContext(options),
      IFlowEngineStateDbContext,
      IFlowEngineOutboxDbContext,
      IFlowEngineCircuitBreakerDbContext
{
    public const string SchemaName = "flowengine";
    public const string MigrationHistoryTable = "__EFMigrationsHistory_FlowEngine";

    // FlowEngine state
    public DbSet<FlowEngineWorkflowRow> Workflows => Set<FlowEngineWorkflowRow>();
    public DbSet<FlowEngineExecutionRow> Executions => Set<FlowEngineExecutionRow>();
    public DbSet<FlowEngineHumanTaskRow> HumanTasks => Set<FlowEngineHumanTaskRow>();
    public DbSet<FlowEngineChildSignalRow> ChildSignals => Set<FlowEngineChildSignalRow>();

    // Outbox - same DbContext as state so SqlExecutionStateStore.SaveWithOutboxAsync can stage
    // entries inside the same SaveChangesAsync.
    public DbSet<FlowEngineOutboxRow> Outbox => Set<FlowEngineOutboxRow>();

    /// <summary>Provides the stage outbox entry operation for task flow flow engine DB context.</summary>
    public void StageOutboxEntry(OutboxEntry entry)
        => OutboxDbContextHelpers.StageOutboxEntry(this, entry);

    // Circuit-breaker store - persists open/half-open state per circuit key across replicas
    // and restarts so a single instance failing doesn't reset the breaker for the others.
    public DbSet<FlowEngineCircuitBreakerRow> CircuitBreakers => Set<FlowEngineCircuitBreakerRow>();

    /// <summary>Handles model creating events for task flow flow engine DB context.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyFlowEngineState();
        modelBuilder.ApplyFlowEngineOutbox();
        modelBuilder.ApplyFlowEngineCircuitBreaker();
    }
}
