using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Provides task item tag behavior for the Infrastructure Configurations layer.</summary>
public class TaskItemTagConfiguration : EntityBaseConfiguration<TaskItemTag, TaskItemTagId>
{
    /// <summary>Configures runtime behavior for this component.</summary>
    public override void Configure(EntityTypeBuilder<TaskItemTag> builder)
    {
        base.Configure(builder);
        builder.ToTable("TaskItemTag");

        builder.HasOne(e => e.TaskItem)
            .WithMany(e => e.TaskItemTags)
            .HasForeignKey(e => new { e.TenantId, e.TaskItemId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Tag)
            .WithMany(e => e.TaskItemTags)
            .HasForeignKey(e => new { e.TenantId, e.TagId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.TenantId, e.TaskItemId, e.TagId })
            .HasDatabaseName("IX_TaskItemTag_TenantId_TaskItemId_TagId")
            .IsUnique();
    }
}
