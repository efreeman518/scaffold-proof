using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>
/// Relational audit sink (D-039). Picked up by the assembly scan. The key is tenant-first so reading one
/// tenant's trail is a range scan; the separate <c>RecordedUtc</c> index is what the retention sweep uses,
/// because that sweep crosses every tenant and no prefix of the primary key covers it.
/// </summary>
public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLogRecord>
{
    public void Configure(EntityTypeBuilder<AuditLogRecord> builder)
    {
        builder.ToTable("AuditLog");
        builder.HasKey(e => new { e.TenantId, e.RecordedUtc, e.Id });
        builder.Property(e => e.TenantId).HasMaxLength(64);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.AuditId).HasMaxLength(128).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(200).IsRequired();
        builder.Property(e => e.EntityKey).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();
        // No HasColumnType on Metadata/Error: nvarchar(max) / text today; jsonb (PostgreSQL) or json
        // (SQL Server 2025) is the upgrade once metadata is queried rather than read back whole.

        builder.HasIndex(e => e.RecordedUtc).HasDatabaseName("IX_AuditLog_RecordedUtc");
    }
}
