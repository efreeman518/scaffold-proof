using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using TaskFlow.Domain.Model;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// D-073: a set-based write (ExecuteUpdate, the insert-if-absent upsert) never runs the EF.Data save pipeline, so it
/// stamps a versioned row the way that pipeline would. Otherwise a client holding the row's old ETag still passes
/// If-Match and overwrites the set-based change.
/// </summary>
internal static class SetBasedWriteStamp
{
    /// <summary>What the save pipeline does to a Modified row: <c>Version + 1</c> and <c>ModifiedAtUtc = now</c>.</summary>
    public static UpdateSettersBuilder<TaskItem> StampModified(this UpdateSettersBuilder<TaskItem> setters, DateTimeOffset now) =>
        setters
            .SetProperty(e => e.Version, e => e.Version + 1)
            .SetProperty(e => e.ModifiedAtUtc, now);

    /// <summary>What the save pipeline does to an Added row: <c>Version = 1</c> and created = modified = <paramref name="now"/>.</summary>
    public static void StampAdded(DbContext db, TaskItem entity, DateTimeOffset now)
    {
        // Entry on an untracked entity does not attach it; it only writes the values through the mapped accessors.
        var entry = db.Entry(entity);
        entry.Property(e => e.Version).CurrentValue = 1;
        entry.Property(e => e.CreatedAtUtc).CurrentValue = now;
        entry.Property(e => e.ModifiedAtUtc).CurrentValue = now;
    }
}
