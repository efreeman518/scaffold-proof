using EF.Domain;
using EF.Domain.Contracts;

namespace TaskFlow.Domain.Model;

/// <summary>
/// TaskFlow entity base over the package <see cref="EntityBase{TId}"/>, which supplies the app-managed
/// <c>long Version</c> concurrency token (D-021). The UTC timestamps (D-024) are the package
/// <see cref="ITimestampedEntity"/> surface: <c>EF.Data.DbContextBase</c> stamps them on every save through EF
/// property entries (Added: created = modified = now and Version = 1; Modified: modified = now, Version + 1), so the
/// domain exposes read-only state and needs no setters or InternalsVisibleTo.
/// </summary>
public abstract class TaskFlowEntityBase<TId> : EntityBase<TId>, ITimestampedEntity
    where TId : struct, IDomainId<TId>
{
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ModifiedAtUtc { get; private set; }

    /// <summary>
    /// Marks the aggregate root as changed so EF puts it in the Modified state and the base context's
    /// save pipeline bumps <see cref="EntityBase{TId}.Version"/> (D-031: one ETag per aggregate).
    /// The written value is overwritten by the save-time stamp; the point is the entry state,
    /// which is the only way a child-only mutation can move the root's concurrency token.
    /// </summary>
    protected void Touch() => ModifiedAtUtc = DateTimeOffset.UtcNow;
}
