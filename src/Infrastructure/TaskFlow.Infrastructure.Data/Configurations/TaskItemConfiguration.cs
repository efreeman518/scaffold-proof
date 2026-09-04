using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Provides task item behavior for the Infrastructure Configurations layer.</summary>
public class TaskItemConfiguration : EntityBaseConfiguration<TaskItem, TaskItemId>
{
    /// <summary>Configures runtime behavior for this component.</summary>
    public override void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        base.Configure(builder);
        builder.ToTable("TaskItem");

        builder.Property(e => e.Title).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.Priority).HasConversion<int>();
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.Features).HasConversion<int>();
        builder.Property(e => e.EstimatedEffort).HasPrecision(10, 2);
        builder.Property(e => e.ActualEffort).HasPrecision(10, 2);

        builder.Property(e => e.SecureDeterministic)
            .HasConversion(v => Encoding.UTF8.GetBytes(v!), v => Encoding.UTF8.GetString(v))
            .HasMaxLength(256);
        builder.Property(e => e.SecureRandom)
            .HasConversion(v => Encoding.UTF8.GetBytes(v!), v => Encoding.UTF8.GetString(v))
            .HasMaxLength(256);

        // DateRange is a domain value object composed from the two first-class columns; never mapped.
        builder.Ignore(e => e.DateRange);

        // Owned JSON document: jsonb on PostgreSQL, json/nvarchar(max) on SQL Server (compatibility level 170).
        builder.OwnsOne(e => e.RecurrencePattern, rp => rp.ToJson());

        builder.HasOne(e => e.ParentTaskItem)
            .WithMany(e => e.SubTasks)
            .HasForeignKey(e => new { e.TenantId, e.ParentTaskItemId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Comments)
            .WithOne(e => e.TaskItem)
            .HasForeignKey(e => new { e.TenantId, e.TaskItemId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.ChecklistItems)
            .WithOne(e => e.TaskItem)
            .HasForeignKey(e => new { e.TenantId, e.TaskItemId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // Tenant-first indexes only (D-022); the Id suffix makes each one a keyset-paging cover.
        builder.HasIndex(e => new { e.TenantId, e.Status, e.Id }).HasDatabaseName("IX_TaskItem_TenantId_Status_Id");
        builder.HasIndex(e => new { e.TenantId, e.Priority, e.Id }).HasDatabaseName("IX_TaskItem_TenantId_Priority_Id");
        builder.HasIndex(e => new { e.TenantId, e.CategoryId, e.Id }).HasDatabaseName("IX_TaskItem_TenantId_CategoryId_Id");
        builder.HasIndex(e => new { e.TenantId, e.DueDate, e.Id }).HasDatabaseName("IX_TaskItem_TenantId_DueDate_Id");
        builder.HasIndex(e => new { e.TenantId, e.ModifiedAtUtc, e.Id }).HasDatabaseName("IX_TaskItem_TenantId_ModifiedAtUtc_Id");
        builder.HasIndex(e => new { e.TenantId, e.Title, e.Id }).HasDatabaseName("IX_TaskItem_TenantId_Title_Id");
        builder.HasIndex(e => new { e.TenantId, e.NextOccurrenceAtUtc }).HasDatabaseName("IX_TaskItem_TenantId_NextOccurrenceAtUtc");
        // Unique over nullable columns: the SQL Server provider emits its own `IS NOT NULL` filter so NULLs stay
        // distinct there too, matching PostgreSQL's default NULLS DISTINCT. No hand-written filter (provider syntax).
        builder.HasIndex(e => new { e.TenantId, e.RecurrenceTemplateId, e.OccurrenceUtc })
            .HasDatabaseName("IX_TaskItem_TenantId_RecurrenceTemplateId_OccurrenceUtc")
            .IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.TerminalAtUtc, e.Status }).HasDatabaseName("IX_TaskItem_TenantId_TerminalAtUtc_Status");
    }
}
