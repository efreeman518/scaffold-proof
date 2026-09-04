using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Provides category behavior for the Infrastructure Configurations layer.</summary>
public class CategoryConfiguration : EntityBaseConfiguration<Category, CategoryId>
{
    /// <summary>Configures runtime behavior for this component.</summary>
    public override void Configure(EntityTypeBuilder<Category> builder)
    {
        base.Configure(builder);
        builder.ToTable("Category");

        builder.Property(e => e.Name).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(500);

        builder.HasOne(e => e.ParentCategory)
            .WithMany(e => e.SubCategories)
            .HasForeignKey(e => new { e.TenantId, e.ParentCategoryId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not SetNull: a composite FK cannot null only its CategoryId half. Callers clear
        // TaskItem.CategoryId for the tenant first (ICategoryRepositoryTrxn.ClearCategoryFromTaskItemsAsync).
        builder.HasMany(e => e.TaskItems)
            .WithOne(e => e.Category)
            .HasForeignKey(e => new { e.TenantId, e.CategoryId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TenantId, e.Name })
            .HasDatabaseName("IX_Category_TenantId_Name")
            .IsUnique();
    }
}
