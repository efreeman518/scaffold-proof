using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>Shared shape of the lease-claimable work tables (D-026). Picked up by the assembly scan.</summary>
public abstract class OperationalWorkConfiguration<TWork> : IEntityTypeConfiguration<TWork>
    where TWork : OperationalWorkBase
{
    protected abstract string TableName { get; }

    public virtual void Configure(EntityTypeBuilder<TWork> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.LeaseOwner).HasMaxLength(128);
        builder.Property(e => e.LastError).HasMaxLength(1024);

        // Dispatch scan: live rows (DeadLetteredAtUtc null) that are due and whose lease is free or expired.
        builder.HasIndex(e => new { e.DeadLetteredAtUtc, e.AvailableAtUtc, e.LeaseExpiresUtc })
            .HasDatabaseName($"IX_{TableName}_Dispatch");
        // Read-back after a claim keys on the token, never on a timestamp (rounding differs per provider).
        builder.HasIndex(e => e.LeaseToken).HasDatabaseName($"IX_{TableName}_LeaseToken");
    }
}

public sealed class OutboxMessageConfiguration : OperationalWorkConfiguration<OutboxMessage>
{
    protected override string TableName => "OutboxMessage";

    public override void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        base.Configure(builder);
        builder.Property(e => e.Destination).HasMaxLength(200).IsRequired();
        builder.Property(e => e.EventType).HasMaxLength(200).IsRequired();
        // No HasColumnType: nvarchar(max) / text today; jsonb (PostgreSQL) or json (SQL Server 2025) is the upgrade.
        builder.Property(e => e.Payload).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(128);
    }
}

public sealed class BlobDeleteWorkConfiguration : OperationalWorkConfiguration<BlobDeleteWork>
{
    protected override string TableName => "BlobDeleteWork";

    public override void Configure(EntityTypeBuilder<BlobDeleteWork> builder)
    {
        base.Configure(builder);
        builder.Property(e => e.ContainerName).HasMaxLength(63).IsRequired();
        builder.Property(e => e.BlobName).HasMaxLength(1024).IsRequired();
    }
}

/// <summary>D-029: one provider-neutral inbox table replaces per-consumer filtered unique indexes.</summary>
public sealed class ConsumerInboxConfiguration : IEntityTypeConfiguration<ConsumerInbox>
{
    public void Configure(EntityTypeBuilder<ConsumerInbox> builder)
    {
        builder.ToTable("ConsumerInbox");
        builder.HasKey(e => new { e.Consumer, e.MessageId });
        builder.Property(e => e.Consumer).HasMaxLength(64);
        // Retention sweeps by processed time.
        builder.HasIndex(e => e.ProcessedAtUtc).HasDatabaseName("IX_ConsumerInbox_ProcessedAtUtc");
    }
}
