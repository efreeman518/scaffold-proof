using EF.Domain.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Shared mapping for every tenant entity: composite key, timestamps; the concurrency token is model-wide.</summary>
public abstract class EntityBaseConfiguration<TEntity, TId> : IEntityTypeConfiguration<TEntity>
    where TEntity : TaskFlowEntityBase<TId>, ITenantEntity<TenantId>
    where TId : struct, IDomainId<TId>
{
    /// <summary>Configures runtime behavior for this component.</summary>
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        // D-022: tenant-first composite primary key. SQL Server clusters the PK by default; on PostgreSQL this
        // btree is the Citus / elastic-cluster distribution key. No IsClustered() anywhere (provider-specific).
        builder.HasKey(e => new { e.TenantId, e.Id });
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();

        // D-021: EF.Domain.EntityBase<TId>.Version is the concurrency token (tokenized for every versioned
        // entity by RegisterVersionConcurrencyTokens in TaskFlowDbContextBase), incremented by
        // EF.Data.DbContextBase.SaveChangesAsync. RowVersion is named rather than lambda-ignored: that
        // member is [Obsolete] as of EF.Domain 1.1.100 (removed in 2.0) and an expression over it would be
        // a warning, i.e. an error here. It is a SQL Server rowversion assumption and is never mapped.
        builder.Ignore("RowVersion");
    }
}
