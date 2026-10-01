using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>
/// Idempotency-key mapping table (D-074). Applied by <see cref="TaskFlowDbContextBase"/> with the provider name (the
/// assembly scan skips it: no parameterless constructor). The UUIDv7 entity id is the key, so inserts append; the
/// unique (TenantId, Scope, Key) index is what makes a concurrent duplicate lose its insert.
/// </summary>
public sealed class IdempotencyKeyConfiguration(string? providerName) : IEntityTypeConfiguration<IdempotencyKeyRecord>
{
    public const int ScopeMaxLength = 64;
    public const int KeyMaxLength = 200;

    /// <summary>Ordinal code-point comparison on SQL Server, so keys that differ only by case stay two keys.</summary>
    public const string SqlServerBinaryCollation = "Latin1_General_100_BIN2";

    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";

    public void Configure(EntityTypeBuilder<IdempotencyKeyRecord> builder)
    {
        builder.ToTable("IdempotencyKey");
        builder.HasKey(e => e.EntityId);
        builder.Property(e => e.EntityId).ValueGeneratedNever();
        builder.Property(e => e.Scope).HasMaxLength(ScopeMaxLength).IsRequired();
        builder.Property(e => e.Key).HasMaxLength(KeyMaxLength).IsRequired();
        // D-030 forced branch: SQL Server's default collation is case-insensitive, so the unique index would merge
        // "abc" and "ABC"; PostgreSQL's default collation is deterministic and already compares them as different.
        // SQL Server still ignores trailing spaces in comparisons under every collation (ANSI padding); HTTP strips
        // a header value's surrounding whitespace before the filter sees it.
        if (string.Equals(providerName, SqlServerProviderName, StringComparison.Ordinal))
        {
            builder.Property(e => e.Scope).UseCollation(SqlServerBinaryCollation);
            builder.Property(e => e.Key).UseCollation(SqlServerBinaryCollation);
        }
        builder.HasIndex(e => new { e.TenantId, e.Scope, e.Key }).IsUnique()
            .HasDatabaseName("UX_IdempotencyKey_TenantId_Scope_Key");
        // The retention sweep deletes by age.
        builder.HasIndex(e => e.CreatedUtc).HasDatabaseName("IX_IdempotencyKey_CreatedUtc");
    }
}
