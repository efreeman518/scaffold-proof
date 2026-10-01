using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>
/// Idempotency-key mapping table (D-074). Picked up by the assembly scan. The UUIDv7 entity id is the key, so
/// inserts append; the unique (TenantId, Scope, Key) index is what makes a concurrent duplicate lose its insert.
/// </summary>
public sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKeyRecord>
{
    public const int ScopeMaxLength = 64;
    public const int KeyMaxLength = 200;

    public void Configure(EntityTypeBuilder<IdempotencyKeyRecord> builder)
    {
        builder.ToTable("IdempotencyKey");
        builder.HasKey(e => e.EntityId);
        builder.Property(e => e.EntityId).ValueGeneratedNever();
        builder.Property(e => e.Scope).HasMaxLength(ScopeMaxLength).IsRequired();
        builder.Property(e => e.Key).HasMaxLength(KeyMaxLength).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.Scope, e.Key }).IsUnique()
            .HasDatabaseName("UX_IdempotencyKey_TenantId_Scope_Key");
        // The retention sweep deletes by age.
        builder.HasIndex(e => e.CreatedUtc).HasDatabaseName("IX_IdempotencyKey_CreatedUtc");
    }
}
