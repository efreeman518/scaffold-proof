using EF.Domain;
using EF.Domain.Contracts;

namespace TaskFlow.Domain.Model;

/// <summary>
/// Provider-neutral concurrency and timestamp surface shared by every persisted TaskFlow entity (D-021, D-024).
/// Values are written only by the persistence layer's <c>VersionTimestampInterceptor</c> through EF property
/// entries, so the domain exposes read-only state and needs no setters or InternalsVisibleTo.
/// </summary>
public interface IVersionedEntity
{
    /// <summary>App-managed monotonic version; 1 after insert, +1 per successful update. Exposed as the ETag.</summary>
    long Version { get; }
    DateTimeOffset CreatedAtUtc { get; }
    DateTimeOffset ModifiedAtUtc { get; }
}

/// <summary>
/// TaskFlow entity base over the package <see cref="EntityBase{TId}"/>.
/// fallback: replace with EF.Domain.EntityBase{TId}.Version when published (package request 1); the package
/// <c>RowVersion</c> is a SQL Server rowversion assumption and is never mapped (see EntityBaseConfiguration).
/// </summary>
public abstract class TaskFlowEntityBase<TId> : EntityBase<TId>, IVersionedEntity
    where TId : struct, IDomainId<TId>
{
    public long Version { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ModifiedAtUtc { get; private set; }
}
