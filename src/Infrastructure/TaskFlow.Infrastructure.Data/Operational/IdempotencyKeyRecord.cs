namespace TaskFlow.Infrastructure.Data.Operational;

/// <summary>
/// Maps a caller's <c>Idempotency-Key</c> header to the UUIDv7 id its create or child add uses (D-074). The row is
/// committed before the write, so a retry, a concurrent duplicate or a recovery after a crash reuses the same id
/// and the existing idempotent-create (GR-17) and child-add (D-073) replay paths deduplicate the write.
/// Not a tenant entity: no query filter, no Version, schema <c>taskflow</c>; the repository filters by tenant.
/// </summary>
public sealed class IdempotencyKeyRecord
{
    /// <summary>The id the write is sent with; a server-generated UUIDv7, so the key is append-ordered.</summary>
    public Guid EntityId { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The route the key belongs to, for example <c>task-item.create</c> or a child add with its root id.</summary>
    public string Scope { get; set; } = null!;
    public string Key { get; set; } = null!;
    public DateTimeOffset CreatedUtc { get; set; }
}
