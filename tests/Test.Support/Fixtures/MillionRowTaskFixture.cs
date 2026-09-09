using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Data;

namespace Test.Support.Fixtures;

/// <summary>
/// Bulk task generator for the scale lanes. The shape matters more than the volume: tenants are skewed
/// (one large tenant plus a long tail, the way real multi-tenant data sits), and the proportions match what
/// the scheduler jobs and the export path are supposed to survive - roughly 8% overdue, 3% recurring
/// templates, and 5% cancelled past the stale-cleanup window.
/// <para>
/// Deterministic by seed, so a failing run at 50k rows reproduces exactly. Rows are inserted with
/// <c>AutoDetectChangesEnabled</c> off and a fresh change tracker per batch, because tracking a million
/// entities is what actually makes a seed like this take hours.
/// </para>
/// </summary>
public sealed class MillionRowTaskFixture
{
    /// <summary>Share of tasks that are overdue and not yet notified.</summary>
    public const double OverdueShare = 0.08;

    /// <summary>Share of tasks that are recurring templates with a due next occurrence.</summary>
    public const double RecurringShare = 0.03;

    /// <summary>Share of tasks that are cancelled and past the 90-day stale-cleanup window.</summary>
    public const double StaleCancelledShare = 0.05;

    /// <summary>Rows per SaveChanges. Large enough to amortize round trips, small enough to bound memory.</summary>
    public const int DefaultBatchSize = 5_000;

    private readonly Random _random;
    private readonly DateTimeOffset _now;

    /// <summary>Tenants the fixture writes to, largest first.</summary>
    public IReadOnlyList<Guid> TenantIds { get; }

    /// <summary>Initializes the fixture with a fixed seed so a run is reproducible.</summary>
    public MillionRowTaskFixture(IReadOnlyList<Guid> tenantIds, DateTimeOffset now, int seed = 20260904)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        if (tenantIds.Count == 0) throw new ArgumentException("At least one tenant is required.", nameof(tenantIds));

        TenantIds = tenantIds;
        _now = now;
        _random = new Random(seed);
    }

    /// <summary>
    /// Writes <paramref name="rowCount"/> tasks and returns how many were written. The caller owns the
    /// context and its transaction scope: at these volumes the right unit of recovery is the batch, not the run.
    /// </summary>
    public async Task<int> SeedAsync(
        TaskFlowDbContextTrxn db, int rowCount, int batchSize = DefaultBatchSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowCount, 1);

        var autoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var written = 0;
            var batch = new List<TaskItem>(batchSize);

            for (var i = 0; i < rowCount; i++)
            {
                batch.Add(Build(i));
                if (batch.Count < batchSize) continue;

                written += await FlushAsync(db, batch, ct);
            }

            if (batch.Count > 0) written += await FlushAsync(db, batch, ct);
            return written;
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    /// <summary>Saves one batch and clears the tracker so memory does not grow with the row count.</summary>
    private static async Task<int> FlushAsync(TaskFlowDbContextTrxn db, List<TaskItem> batch, CancellationToken ct)
    {
        db.AddRange(batch);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
        db.ChangeTracker.Clear();

        var written = batch.Count;
        batch.Clear();
        return written;
    }

    /// <summary>Builds one task in the documented mix.</summary>
    private TaskItem Build(int index)
    {
        var tenantId = DomainId.From<TenantId>(PickTenant());
        var roll = _random.NextDouble();

        var task = TaskItem.Create(
            tenantId,
            $"Seeded task {index:D8}",
            $"Generated row {index}",
            (Priority)_random.Next(0, 5)).Value!;

        if (roll < StaleCancelledShare)
        {
            // Cancelled long enough ago to be inside the stale-cleanup window.
            task.TransitionStatus(TaskItemStatus.Cancelled);
            task.UpdateDateRange(null, _now.AddDays(-_random.Next(120, 400)));
            return task;
        }

        if (roll < StaleCancelledShare + RecurringShare)
        {
            // A recurring template whose next occurrence is already due.
            task.Update(features: TaskFeatures.Recurring);
            task.UpdateDateRange(null, _now.AddDays(-_random.Next(1, 30)));
            task.UpdateRecurrencePattern(new RecurrencePattern
            {
                Frequency = RecurrencePattern.Daily,
                Interval = 1 + _random.Next(0, 6),
                EndDate = _now.AddYears(1)
            });
            return task;
        }

        if (roll < StaleCancelledShare + RecurringShare + OverdueShare)
        {
            // Past due, still open, never announced.
            task.UpdateDateRange(null, _now.AddDays(-_random.Next(1, 60)));
            return task;
        }

        task.UpdateDateRange(_now.AddDays(-_random.Next(0, 30)), _now.AddDays(_random.Next(1, 90)));
        return task;
    }

    /// <summary>
    /// Skewed tenant pick: the first tenant takes roughly half the rows, the rest split the remainder.
    /// A uniform split would hide exactly the problem tenant-partitioned indexes exist to solve.
    /// </summary>
    private Guid PickTenant() =>
        TenantIds.Count == 1 || _random.NextDouble() < 0.5
            ? TenantIds[0]
            : TenantIds[_random.Next(1, TenantIds.Count)];
}
