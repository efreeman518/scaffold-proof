using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Provides comment behavior for the Infrastructure Configurations layer.</summary>
public class CommentConfiguration : EntityBaseConfiguration<Comment, CommentId>
{
    /// <summary>Configures runtime behavior for this component.</summary>
    public override void Configure(EntityTypeBuilder<Comment> builder)
    {
        base.Configure(builder);
        builder.ToTable("Comment");

        builder.Property(e => e.Body).HasMaxLength(2000).IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.TaskItemId, e.Id })
            .HasDatabaseName("IX_Comment_TenantId_TaskItemId_Id");
    }
}
