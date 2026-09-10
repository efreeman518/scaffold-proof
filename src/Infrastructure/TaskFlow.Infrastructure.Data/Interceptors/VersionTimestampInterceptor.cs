using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskFlow.Domain.Model;

namespace TaskFlow.Infrastructure.Data.Interceptors;

/// <summary>
/// D-024: maintains the UTC timestamps of every <see cref="ITimestampedEntity"/>, plus the insert baseline
/// of the D-021 concurrency token. Added: CreatedAtUtc = ModifiedAtUtc = now, Version = 1. Modified:
/// ModifiedAtUtc = now.
/// <para>
/// The version INCREMENT is not here: <c>EF.Data.DbContextBase.SaveChangesAsync</c> walks every Modified
/// <c>EF.Domain.Contracts.IVersionedEntity</c> entry, sets the property's OriginalValue to the pre-increment
/// value and its CurrentValue to that value plus one, which is what makes EF emit
/// <c>WHERE Version = @original</c> (package request 2). It does not touch Added entries, so the
/// "1 after insert, +1 per successful update" contract still needs the baseline stamped here - the package
/// alone would leave a fresh row at 0.
/// </para>
/// </summary>
public sealed class VersionTimestampInterceptor(TimeProvider? timeProvider = null) : SaveChangesInterceptor
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null) return;

        var now = _timeProvider.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries<ITimestampedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property<DateTimeOffset>(nameof(ITimestampedEntity.CreatedAtUtc)).CurrentValue = now;
                    entry.Property<DateTimeOffset>(nameof(ITimestampedEntity.ModifiedAtUtc)).CurrentValue = now;
                    entry.Property<long>(nameof(EF.Domain.Contracts.IVersionedEntity.Version)).CurrentValue = 1;
                    break;
                case EntityState.Modified:
                    entry.Property<DateTimeOffset>(nameof(ITimestampedEntity.ModifiedAtUtc)).CurrentValue = now;
                    break;
            }
        }
    }
}
