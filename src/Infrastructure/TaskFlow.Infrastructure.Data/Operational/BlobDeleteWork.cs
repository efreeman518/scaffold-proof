using EF.Data.Outbox;

namespace TaskFlow.Infrastructure.Data.Operational;

/// <summary>
/// Deferred blob deletion so a domain delete never waits on storage (D-026; 404 counts as success). A leased work
/// row: EF.Data.Outbox owns the lease, attempt and dead-letter columns; this table adds the tenant and the blob.
/// Not a tenant entity: no query filter, no Version, schema <c>taskflow</c>.
/// </summary>
public sealed class BlobDeleteWork : LeasedWorkItem
{
    public Guid TenantId { get; set; }
    public string ContainerName { get; set; } = null!;
    public string BlobName { get; set; } = null!;
}
