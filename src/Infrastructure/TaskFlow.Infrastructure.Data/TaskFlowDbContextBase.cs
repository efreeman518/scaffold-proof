using EF.Data;
using EF.Data.Encryption;
using EF.Domain.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data.Configurations;
using TaskFlow.Infrastructure.Data.Conventions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Data.ReadModel;

namespace TaskFlow.Infrastructure.Data;

/// <summary>
/// Shared EF model for read and write DbContexts. Centralizes schema, table naming, provider-neutral
/// conventions, entity configurations, and tenant query filters. Contains no provider branch (D-030).
/// </summary>
public abstract class TaskFlowDbContextBase(DbContextOptions options) : DbContextBase<string, Guid?>(options)
{
    public const string SchemaName = "taskflow";
    public const string MigrationHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Registers typed ID conversions and the provider-neutral scalar conventions before EF discovers the model:
    /// decimal precision (18,4) unless a property says otherwise, and UTC normalization for every temporal value (D-024).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.RegisterDomainIdConversions(typeof(TenantId).Assembly);
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    /// <summary>
    /// Builds the TaskFlow model once for derived contexts. Derived contexts only choose tracking
    /// and connection behavior; entity mapping stays identical.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TaskFlowDbContextBase).Assembly);
        // TaskItemConfiguration has no parameterless constructor (the assembly scan skips it): it binds the
        // secure-column converters to the process encryptor carried by the options (D-023).
        modelBuilder.ApplyConfiguration(new TaskItemConfiguration(this.GetColumnEncryptor()));
        ConfigureVectorSearch(modelBuilder);
        SetTableNames(modelBuilder);
        ConfigureTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// The one model-level provider branch the solution allows (D-030), and it is forced: <c>vector</c> is a
    /// PostgreSQL extension type with no SQL Server equivalent that EF can map today, and the HNSW index needs
    /// an Npgsql-only <c>HasMethod</c>/<c>HasOperators</c> pair. Mapping it unconditionally would put a column
    /// SQL Server cannot create into the SQL Server migration snapshot.
    /// <para>
    /// Future arm: SQL Server 2025 ships a native <c>VECTOR</c> type with <c>VECTOR_DISTANCE</c>. When the EF
    /// Core SQL Server provider maps it, this becomes a two-arm branch here rather than a Postgres-only entity,
    /// and <c>PgVectorSearchService</c>'s startup guard loses its reason to exist.
    /// </para>
    /// </summary>
    private void ConfigureVectorSearch(ModelBuilder modelBuilder)
    {
        if (!string.Equals(Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal)) return;

        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfiguration(new TaskItemEmbeddingConfiguration(TaskItemEmbedding.DefaultDimensions));
    }

    /// <summary>Assembly-qualified-free provider name Npgsql reports through <see cref="DatabaseFacade.ProviderName"/>.</summary>
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>Provides the set table names operation for task flow DB context base.</summary>
    private static void SetTableNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Do not force a table name for owned types; they share the owner's table
            if (entity.IsOwned()) continue;

            var current = entity.GetTableName();
            if (string.IsNullOrWhiteSpace(current))
            {
                entity.SetTableName(entity.DisplayName());
            }
        }
    }

    /// <summary>Configures tenant query filters behavior for this component.</summary>
    private void ConfigureTenantQueryFilters(ModelBuilder modelBuilder)
    {
        var tenantEntityClrTypes = modelBuilder.Model.GetEntityTypes()
            .Where(et => typeof(ITenantEntity<TenantId>).IsAssignableFrom(et.ClrType))
            .Select(et => et.ClrType);

        foreach (var clrType in tenantEntityClrTypes)
        {
            var filter = BuildTenantFilter(clrType);
            modelBuilder.Entity(clrType).HasQueryFilter(filter);
        }
    }

    // DbSets
    public DbSet<Category> Categories { get; set; } = null!;
    public DbSet<Tag> Tags { get; set; } = null!;
    public DbSet<TaskItem> TaskItems { get; set; } = null!;
    public DbSet<Comment> Comments { get; set; } = null!;
    public DbSet<ChecklistItem> ChecklistItems { get; set; } = null!;
    public DbSet<Attachment> Attachments { get; set; } = null!;
    public DbSet<TaskItemTag> TaskItemTags { get; set; } = null!;

    // Operational work tables (D-026, D-029): not tenant entities, no query filter, no Version.
    public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;
    public DbSet<BlobDeleteWork> BlobDeleteWork { get; set; } = null!;
    public DbSet<ConsumerInbox> ConsumerInbox { get; set; } = null!;

    // Portable-lane read model and audit sink (D-038, D-039): same rules as the operational tables - not
    // tenant entities, no query filter, no Version. Declared on the shared base so both contexts see them:
    // the projection writes and counter patches run on Trxn and the list/get endpoints read on Query (D-027).
    public DbSet<TaskViewRecord> TaskViews { get; set; } = null!;
    public DbSet<AuditLogRecord> AuditLog { get; set; } = null!;
}
