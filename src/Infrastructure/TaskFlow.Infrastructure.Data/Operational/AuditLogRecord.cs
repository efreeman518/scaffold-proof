namespace TaskFlow.Infrastructure.Data.Operational;

/// <summary>
/// Relational audit sink row (D-039), the portable-lane counterpart of the Azure Table audit entity. Not a
/// tenant entity: no query filter and no Version, because the audit trail is written once and never updated,
/// and stamping an audit row with audit metadata would recurse. <c>TenantId</c> is non-null - a system entry
/// carries the configured sentinel (<c>AuditLogStorageSettings.NullTenantPartitionKey</c>) so it stays
/// queryable inside the tenant-first key, exactly like the Table partition key does.
/// </summary>
public sealed class AuditLogRecord
{
    public string TenantId { get; set; } = null!;
    public DateTimeOffset RecordedUtc { get; set; }
    public Guid Id { get; set; }
    public string AuditId { get; set; } = null!;
    public string EntityType { get; set; } = null!;
    public string EntityKey { get; set; } = null!;
    public string Action { get; set; } = null!;
    public string Status { get; set; } = null!;
    public long StartTimeTicks { get; set; }
    public long ElapsedTimeTicks { get; set; }
    public string? Metadata { get; set; }
    public string? Error { get; set; }
}
