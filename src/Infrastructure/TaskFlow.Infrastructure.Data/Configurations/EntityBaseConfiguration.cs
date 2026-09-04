using EF.Domain.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Shared mapping for every tenant entity: composite key, app-managed concurrency token, timestamps.</summary>
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

        // D-021: provider-neutral concurrency token maintained by VersionTimestampInterceptor; the package
        // RowVersion (SQL Server rowversion) is never mapped.
        builder.Property(e => e.Version).IsConcurrencyToken();
        builder.Ignore(e => e.RowVersion);
    }
}
