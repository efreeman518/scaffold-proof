using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Cross-tenant system access for the scheduler jobs, over the write context with
/// <c>IgnoreQueryFilters()</c>. Every scan is keyset-paged by the clustered <c>(TenantId, Id)</c> key so a
/// job's cost is bounded by the rows it actually touches, not by how far into the table it has walked. Whole walks
/// use the package <c>StreamKeysetPagesAsync</c>; the stale batch resumes it from a caller-held <c>KeysetPosition</c>.
/// </summary>
public sealed class TaskItemSystemRepository(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null)
    : RepositoryBase<TaskFlowDbContextTrxn, string, Guid?>(db), ITaskItemSystemRepository
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async IAsyncEnumerable<OverdueTaskRow> StreamOverdueAsync(
        DateTimeOffset asOfUtc, int pageSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pages = OverdueCandidates(asOfUtc).AsNoTracking().StreamKeysetPagesAsync(
            e => new OverdueTaskRow(e.TenantId.Value, e.Id.Value, e.DueDate!.Value),
            e => e.TenantId, e => e.Id, pageSize, ct);
        await foreach (var page in pages.ConfigureAwait(false))
        {
            foreach (var row in page) yield return row;
        }
    }

    /// <inheritdoc />
    public async Task<bool> MarkOverdueNotifiedAsync(
        Guid tenantId, Guid id, DateTimeOffset asOfUtc, CancellationToken ct = default)
    {
        var typedId = TaskItemId.From(id);
        var typedTenantId = TenantId.From(tenantId);

        // The candidate predicate is restated here, not trusted from the scan: between the two statements a
        // task can be completed or rescheduled, and marking it then would suppress a notification it is owed.
        // Per row, not one IN-list update: a count cannot say which rows another replica marked first, and
        // RETURNING is not portable through ExecuteUpdate on both providers.
        var affected = await OverdueCandidates(asOfUtc)
            .Where(e => e.TenantId == typedTenantId && e.Id == typedId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.OverdueNotifiedForDueDate, e => e.DueDate).StampModified(DB.Clock.GetUtcNow()), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return affected > 0;
    }

    /// <inheritdoc />
    // Hand-rolled keyset over TenantId alone: the package walk needs a unique tie-breaker, and a distinct tenant id
    // has only the one key. Each page resumes after the last tenant it returned, so its cost is bounded by the
    // tenants it reads, not by how many tasks the earlier tenants had.
    public async IAsyncEnumerable<Guid> StreamTenantsWithTaggedOpenTasksDueAsync(
        string tagName, DateTimeOffset dueBefore, int pageSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        // Same normalization as the task search's tagName filter (TaskItemRepositoryQuery).
        var upperTagName = tagName.Trim().ToUpperInvariant();
        var candidates = DB.Set<TaskItem>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.DueDate != null && e.DueDate <= dueBefore
                && e.Status != TaskItemStatus.Completed
                && e.Status != TaskItemStatus.Cancelled
                && e.TaskItemTags.Any(tt => tt.Tag.Name.Trim().ToUpper() == upperTagName));

        TenantId? after = null;
        while (true)
        {
            var page = after is { } position ? candidates.Where(e => e.TenantId > position) : candidates;
            var tenants = await page.Select(e => e.TenantId).Distinct().OrderBy(t => t).Take(pageSize)
                .ToListAsync(ct).ConfigureAwait(ConfigureAwaitOptions.None);
            foreach (var tenant in tenants) yield return tenant.Value;
            if (tenants.Count < pageSize) yield break;
            after = tenants[^1];
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TaskItem> StreamDueTemplatesAsync(
        DateTimeOffset asOfUtc, int pageSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pages = DueTemplates(asOfUtc).AsNoTracking().StreamKeysetPagesAsync(e => e.TenantId, e => e.Id, pageSize, ct);
        await foreach (var page in pages.ConfigureAwait(false))
        {
            foreach (var template in page) yield return template;
        }
    }

    /// <inheritdoc />
    // D-028: MERGE on SQL Server, ON CONFLICT DO NOTHING on PostgreSQL, keyed on the unique
    // IX_TaskItem_TenantId_RecurrenceTemplateId_OccurrenceUtc. A null whenMatched is the package's
    // insert-if-absent arm, so a template that already produced this occurrence is left untouched and the
    // statement affects no row.
    // The upsert bypasses the save pipeline, so the occurrence is stamped as a tracked insert would be (D-073).
    public async Task<bool> InsertOccurrenceIfAbsentAsync(TaskItem occurrence, CancellationToken ct = default)
    {
        SetBasedWriteStamp.StampAdded(DB, occurrence, DB.Clock.GetUtcNow());
        return await UpsertAsync(
            occurrence,
            e => new { e.TenantId, e.RecurrenceTemplateId, e.OccurrenceUtc },
            cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> AdvanceNextOccurrenceAsync(
        Guid tenantId, Guid templateId, DateTimeOffset expectedNextUtc, DateTimeOffset? newNextUtc, CancellationToken ct = default)
    {
        var typedTenantId = TenantId.From(tenantId);
        var typedTemplateId = TaskItemId.From(templateId);

        var affected = await DB.Set<TaskItem>()
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == typedTenantId && e.Id == typedTemplateId
                && e.NextOccurrenceAtUtc == expectedNextUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextOccurrenceAtUtc, newNextUtc).StampModified(DB.Clock.GetUtcNow()), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StaleTaskRow>> GetStaleBatchAsync(
        DateTimeOffset cutoffUtc, StaleTaskRow? after, int pageSize, CancellationToken ct = default)
    {
        // The caller holds the position across its delete batches, so each call is the first page of a walk
        // resumed after the last row it processed; the deletes behind the position do not shift it.
        var resume = after is { } position
            ? new KeysetPosition<TenantId, TaskItemId>(TenantId.From(position.TenantId), TaskItemId.From(position.Id))
            : (KeysetPosition<TenantId, TaskItemId>?)null;
        var pages = StaleCandidates(cutoffUtc).AsNoTracking().StreamKeysetPagesAsync(
            e => new StaleTaskRow(e.TenantId.Value, e.Id.Value),
            e => e.TenantId, e => e.Id, resume, pageSize, ct);
        await foreach (var page in pages.ConfigureAwait(false))
        {
            return page;
        }

        return [];
    }

    /// <inheritdoc />
    public async Task<int> StageBlobDeletesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return 0;
        var typedTenantId = TenantId.From(tenantId);
        var ids = taskIds.ToList();

        var attachments = await DB.Set<Attachment>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == typedTenantId
                && a.OwnerType == AttachmentOwnerType.TaskItem
                && ids.Contains(a.OwnerId)
                // Only uploaded content has a stored key; a metadata-only attachment wrote no blob.
                && a.StorageKey != null)
            .Select(a => new { AttachmentId = a.Id.Value, StorageKey = a.StorageKey! })
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        if (attachments.Count == 0) return 0;

        var now = _timeProvider.GetUtcNow();
        foreach (var attachment in attachments)
        {
            DB.BlobDeleteWork.Add(AttachmentBlobDeleteWork.ForAttachment(tenantId, attachment.AttachmentId, attachment.StorageKey, now));
        }

        await DB.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return attachments.Count;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteStaleTaskAsync(
        Guid tenantId, Guid taskId, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        var typedTenantId = TenantId.From(tenantId);
        var typedId = TaskItemId.From(taskId);

        // Predicate restated: a task reopened between the scan and this statement must survive. Per row, not one
        // IN-list delete: a count cannot say which tasks another run removed first (RETURNING is not portable
        // through ExecuteDelete on both providers), and only the remover may queue the task's blob work.
        var affected = await StaleCandidates(cutoffUtc)
            .Where(e => e.TenantId == typedTenantId && e.Id == typedId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return affected > 0;
    }

    /// <inheritdoc />
    public Task<int> DeleteAttachmentsAsync(Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return Task.FromResult(0);
        var typedTenantId = TenantId.From(tenantId);
        var ids = taskIds.ToList();

        return DB.Set<Attachment>()
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == typedTenantId
                && a.OwnerType == AttachmentOwnerType.TaskItem
                && ids.Contains(a.OwnerId))
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    // Saves inside work keep the default acceptAllChangesOnSuccess: true. EF.Data's alternative (accept after the
    // commit, so a retry re-sends the same changes) does not fit: work re-stages its rows on every attempt, and they
    // would collide with rows still tracked as Added. Clearing per attempt, as DbContextBase.RetryOnConcurrencyAsync
    // does, re-runs the reads and guards against the database as it now is (D-009, D-073).
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        DB.ChangeTracker.DetectChanges();
        if (DB.ChangeTracker.HasChanges())
            throw new InvalidOperationException(
                "ExecuteInTransactionAsync clears the change tracker on each attempt; save or discard the pending changes first.");

        T result = default!;
        await ResilientTransaction.New(DB).ExecuteAsync(async token =>
        {
            DB.ChangeTracker.Clear();
            result = await work(token).ConfigureAwait(ConfigureAwaitOptions.None);
        }, ct).ConfigureAwait(ConfigureAwaitOptions.None);
        return result;
    }

    /// <inheritdoc />
    // Throw, not ClientWins: the rows saved here are operational (outbox, blob-delete work) and carry no
    // concurrency token, so a conflict would mean the unit of work is not what this job thinks it is.
    // Not named SaveChangesAsync: RepositoryBase.SaveChangesAsync(ct) is the policy-free overload (plain
    // DbContext.SaveChangesAsync); every save on this path carries the Throw policy (D-032).
    public Task<int> SaveWithThrowPolicyAsync(CancellationToken ct = default) =>
        DB.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);

    /// <summary>Past due, still open, and not yet announced for this particular due date.</summary>
    private IQueryable<TaskItem> OverdueCandidates(DateTimeOffset asOfUtc) =>
        DB.Set<TaskItem>()
            .IgnoreQueryFilters()
            .Where(e => e.DueDate != null && e.DueDate < asOfUtc
                && e.Status != TaskItemStatus.Completed
                && e.Status != TaskItemStatus.Cancelled
                // NULL != x is unknown in SQL, so the null arm has to be spelled out or first-time
                // candidates never match.
                && (e.OverdueNotifiedForDueDate == null || e.OverdueNotifiedForDueDate != e.DueDate));

    /// <summary>Recurring templates whose next occurrence has come due. Uses IX_TaskItem_TenantId_NextOccurrenceAtUtc.</summary>
    private IQueryable<TaskItem> DueTemplates(DateTimeOffset asOfUtc) =>
        DB.Set<TaskItem>()
            .IgnoreQueryFilters()
            .Where(e => (e.Features & TaskFeatures.Recurring) == TaskFeatures.Recurring
                && e.NextOccurrenceAtUtc != null
                && e.NextOccurrenceAtUtc <= asOfUtc
                && e.Status != TaskItemStatus.Cancelled);

    /// <summary>Cancelled past retention, with no surviving subtask. Uses IX_TaskItem_TenantId_TerminalAtUtc_Status.</summary>
    private IQueryable<TaskItem> StaleCandidates(DateTimeOffset cutoffUtc) =>
        DB.Set<TaskItem>()
            .IgnoreQueryFilters()
            .Where(e => e.Status == TaskItemStatus.Cancelled
                && e.TerminalAtUtc != null
                && e.TerminalAtUtc < cutoffUtc
                // The subtask FK is Restrict: a parent with children still present cannot be deleted, and
                // becomes deletable on a later run once they are gone.
                && !e.SubTasks.Any());
}
