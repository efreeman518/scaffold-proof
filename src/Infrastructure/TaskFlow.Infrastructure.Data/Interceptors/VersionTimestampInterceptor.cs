using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskFlow.Domain.Model;

namespace TaskFlow.Infrastructure.Data.Interceptors;

/// <summary>
/// D-021 / D-024: maintains the app-managed concurrency token and UTC timestamps for every
/// <see cref="IVersionedEntity"/>. Added: CreatedAtUtc = ModifiedAtUtc = now, Version = 1.
/// Modified: ModifiedAtUtc = now, Version = original + 1 with the original left untouched so EF emits
/// <c>WHERE Version = @original</c>. Deriving from the ORIGINAL value (not the current one) keeps the
/// bump correct when the package ClientWins retry refreshes originals from the database and saves again.
/// fallback: replace with EF.Data DbContextBase Version handling when published (package request 2).
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
        foreach (var entry in context.ChangeTracker.Entries<IVersionedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property<DateTimeOffset>(nameof(IVersionedEntity.CreatedAtUtc)).CurrentValue = now;
                    entry.Property<DateTimeOffset>(nameof(IVersionedEntity.ModifiedAtUtc)).CurrentValue = now;
                    entry.Property<long>(nameof(IVersionedEntity.Version)).CurrentValue = 1;
                    break;
                case EntityState.Modified:
                    entry.Property<DateTimeOffset>(nameof(IVersionedEntity.ModifiedAtUtc)).CurrentValue = now;
                    var version = entry.Property<long>(nameof(IVersionedEntity.Version));
                    version.CurrentValue = version.OriginalValue + 1;
                    break;
            }
        }
    }
}
