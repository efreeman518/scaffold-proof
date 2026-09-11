using EF.Domain;
using EF.Domain.Contracts;

namespace TaskFlow.Domain.Model;

/// <summary>
/// UTC timestamp surface shared by every persisted TaskFlow entity (D-024). Values are written only by the
/// persistence layer's <c>VersionTimestampInterceptor</c> through EF property entries, so the domain exposes
/// read-only state and needs no setters or InternalsVisibleTo.
/// <para>
/// The concurrency token itself is no longer declared here: <see cref="EntityBase{TId}"/> carries
/// <c>Version</c> and implements <see cref="IVersionedEntity"/> as of EF.Domain 1.1.100 (package request 1),
/// and <c>EF.Data.DbContextBase.SaveChangesAsync</c> owns the increment (package request 2).
/// </para>
/// </summary>
public interface ITimestampedEntity
{
    DateTimeOffset CreatedAtUtc { get; }
    DateTimeOffset ModifiedAtUtc { get; }
}

/// <summary>
/// TaskFlow entity base over the package <see cref="EntityBase{TId}"/>, which supplies the app-managed
/// <c>long Version</c> concurrency token (D-021). Only the timestamps are TaskFlow's own.
/// </summary>
public abstract class TaskFlowEntityBase<TId> : EntityBase<TId>, ITimestampedEntity
    where TId : struct, IDomainId<TId>
{
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ModifiedAtUtc { get; private set; }

    /// <summary>
    /// Marks the aggregate root as changed so EF puts it in the Modified state and the base context's
    /// <c>SaveChangesAsync</c> bumps <see cref="EntityBase{TId}.Version"/> (D-031: one ETag per aggregate).
    /// The written value is overwritten by <c>VersionTimestampInterceptor</c>; the point is the entry state,
    /// which is the only way a child-only mutation can move the root's concurrency token.
    /// </summary>
    protected void Touch() => ModifiedAtUtc = DateTimeOffset.UtcNow;
}
