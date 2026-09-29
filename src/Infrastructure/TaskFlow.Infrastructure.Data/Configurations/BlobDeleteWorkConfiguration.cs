using EF.Data.Outbox;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Data.Configurations;

/// <summary>
/// Leased work table for deferred blob deletes (D-026): the package mapping (key, lease columns, dispatch and
/// lease-token indexes) plus the blob columns. Picked up by the assembly scan.
/// </summary>
public sealed class BlobDeleteWorkConfiguration()
    : LeasedWorkConfiguration<BlobDeleteWork>("BlobDeleteWork", TaskFlowDbContextBase.SchemaName)
{
    public override void Configure(EntityTypeBuilder<BlobDeleteWork> builder)
    {
        base.Configure(builder);
        builder.Property(e => e.ContainerName).HasMaxLength(63).IsRequired();
        builder.Property(e => e.BlobName).HasMaxLength(1024).IsRequired();
    }
}
