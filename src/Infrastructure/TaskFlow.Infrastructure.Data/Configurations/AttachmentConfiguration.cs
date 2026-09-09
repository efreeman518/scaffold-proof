using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Provides attachment behavior for the Infrastructure Configurations layer.</summary>
public class AttachmentConfiguration : EntityBaseConfiguration<Attachment, AttachmentId>
{
    /// <summary>Configures runtime behavior for this component.</summary>
    public override void Configure(EntityTypeBuilder<Attachment> builder)
    {
        base.Configure(builder);
        builder.ToTable("Attachment");

        builder.Property(e => e.FileName).HasMaxLength(255).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.StorageUri).HasMaxLength(2000).IsRequired();
        builder.Property(e => e.OwnerType).HasConversion<int>();

        // Polymorphic owner lookup, tenant-first (D-022).
        builder.HasIndex(e => new { e.TenantId, e.OwnerType, e.OwnerId, e.Id })
            .HasDatabaseName("IX_Attachment_TenantId_OwnerType_OwnerId_Id");
    }
}
