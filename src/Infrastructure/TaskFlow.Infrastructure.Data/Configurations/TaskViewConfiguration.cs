using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Infrastructure.Data.ReadModel;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>
/// Relational TaskView read model (D-038). Picked up by the assembly scan. Tenant-first key, so a tenant's
/// page is one contiguous index range - the relational equivalent of the Cosmos partition key.
/// </summary>
public sealed class TaskViewConfiguration : IEntityTypeConfiguration<TaskViewRecord>
{
    public void Configure(EntityTypeBuilder<TaskViewRecord> builder)
    {
        builder.ToTable("TaskView");
        builder.HasKey(e => new { e.TenantId, e.Id });
        // Both keys are the caller's opaque strings (Cosmos ids are strings, not necessarily GUIDs), sized
        // for a GUID in any format.
        builder.Property(e => e.TenantId).HasMaxLength(64);
        builder.Property(e => e.Id).HasMaxLength(64);
        builder.Property(e => e.Title).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Priority).HasMaxLength(32).IsRequired();
        builder.Property(e => e.CategoryName).HasMaxLength(200);
        // No HasColumnType: nvarchar(max) / text today. jsonb plus a GIN index (PostgreSQL) or json
        // (SQL Server 2025) is the upgrade once anything queries inside the body.
        builder.Property(e => e.Document).IsRequired();

        // The list query's keyset order. Descending on both trailing columns so the index serves the page
        // scan directly instead of a sort.
        builder.HasIndex(e => new { e.TenantId, e.LastModifiedUtc, e.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_TaskView_TenantId_LastModifiedUtc_Id");
    }
}
