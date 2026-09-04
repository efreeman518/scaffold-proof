using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Provides tag behavior for the Infrastructure Configurations layer.</summary>
public class TagConfiguration : EntityBaseConfiguration<Tag, TagId>
{
    /// <summary>Configures runtime behavior for this component.</summary>
    public override void Configure(EntityTypeBuilder<Tag> builder)
    {
        base.Configure(builder);
        builder.ToTable("Tag");

        builder.Property(e => e.Name).HasMaxLength(50).IsRequired();
        builder.Property(e => e.Color).HasMaxLength(7);

        builder.HasIndex(e => new { e.TenantId, e.Name })
            .HasDatabaseName("IX_Tag_TenantId_Name")
            .IsUnique();
    }
}
