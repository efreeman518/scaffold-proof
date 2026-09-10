using EF.Common;
using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Constants;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Cross-tenant system access for the scheduler jobs, over the write context with
/// <c>IgnoreQueryFilters()</c>. Every scan is keyset-paged by the clustered <c>(TenantId, Id)</c> key so a
/// job's cost is bounded by the rows it actually touches, not by how far into the table it has walked.
/// </summary>
public sealed class TaskItemSystemRepository(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null)
    : RepositoryBase<TaskFlowDbContextTrxn, string, Guid?>(db), ITaskItemSystemRepository
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async IAsyncEnumerable<OverdueTaskRow> StreamOverdueAsync(
        DateTimeOffset asOfUtc, int pageSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        (Guid TenantId, Guid Id)? after = null;
        while (true)
        {
            var page = await Keyset(OverdueCandidates(asOfUtc), after)
                .Take(pageSize)
                .Select(e => new OverdueTaskRow(e.TenantId.Value, e.Id.Value, e.DueDate!.Value))
                .ToListAsync(ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);

            foreach (var row in page) yield return row;

            if (page.Count < pageSize) yield break;
            after = (page[^1].TenantId, page[^1].Id);
        }
    }

    /// <inheritdoc />
    public Task<int> MarkOverdueNotifiedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, DateTimeOffset asOfUtc, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Task.FromResult(0);
        var typedIds = ids.Select(DomainId.From<TaskItemId>).ToList();
        var typedTenantId = DomainId.From<TenantId>(tenantId);

        // The candidate predicate is restated here, not trusted from the scan: between the two statements a
        // task can be completed or rescheduled, and marking it then would suppress a notification it is owed.
        return OverdueCandidates(asOfUtc)
            .Where(e => e.TenantId == typedTenantId && typedIds.Contains(e.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.OverdueNotifiedForDueDate, e => e.DueDate), ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TaskItem> StreamDueTemplatesAsync(
        DateTimeOffset asOfUtc, int pageSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        (Guid TenantId, Guid Id)? after = null;
        while (true)
        {
            var page = await Keyset(DueTemplates(asOfUtc), after)
                .Take(pageSize)
                .ToListAsync(ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);

            foreach (var template in page) yield return template;

            if (page.Count < pageSize) yield break;
            after = (page[^1].TenantId.Value, page[^1].Id.Value);
        }
    }

    /// <inheritdoc />
    // D-028: MERGE on SQL Server, ON CONFLICT DO NOTHING on PostgreSQL, keyed on the unique
    // IX_TaskItem_TenantId_RecurrenceTemplateId_OccurrenceUtc. A null whenMatched is the package's
    // insert-if-absent arm, so a template that already produced this occurrence is left untouched.
    public Task<int> UpsertOccurrencesAsync(IReadOnlyCollection<TaskItem> occurrences, CancellationToken ct = default) =>
        UpsertRangeAsync(
            occurrences,
            e => new { e.TenantId, e.RecurrenceTemplateId, e.OccurrenceUtc },
            cancellationToken: ct);

    /// <inheritdoc />
    public async Task<bool> AdvanceNextOccurrenceAsync(
        Guid tenantId, Guid templateId, DateTimeOffset expectedNextUtc, DateTimeOffset? newNextUtc, CancellationToken ct = default)
    {
        var typedTenantId = DomainId.From<TenantId>(tenantId);
        var typedTemplateId = DomainId.From<TaskItemId>(templateId);

        var affected = await DB.Set<TaskItem>()
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == typedTenantId && e.Id == typedTemplateId
                && e.NextOccurrenceAtUtc == expectedNextUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextOccurrenceAtUtc, newNextUtc), ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StaleTaskRow>> GetStaleBatchAsync(
        DateTimeOffset cutoffUtc, StaleTaskRow? after, int pageSize, CancellationToken ct = default)
    {
        var cursor = after is { } position ? (position.TenantId, position.Id) : ((Guid, Guid)?)null;
        return await Keyset(StaleCandidates(cutoffUtc), cursor)
            .Take(pageSize)
            .Select(e => new StaleTaskRow(e.TenantId.Value, e.Id.Value))
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    public async Task<int> StageBlobDeletesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return 0;
        var typedTenantId = DomainId.From<TenantId>(tenantId);
        var ids = taskIds.ToList();

        var attachments = await DB.Set<Attachment>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == typedTenantId
                && a.OwnerType == AttachmentOwnerType.TaskItem
                && ids.Contains(a.OwnerId))
            .Select(a => new { AttachmentId = a.Id.Value, a.OwnerId, a.FileName })
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        if (attachments.Count == 0) return 0;

        var now = _timeProvider.GetUtcNow();
        foreach (var attachment in attachments)
        {
            DB.BlobDeleteWork.Add(new BlobDeleteWork
            {
                // Deterministic: a retried cleanup batch reuses the same work row instead of queueing the
                // same blob twice. The drainer treats a 404 as success anyway, but duplicates are noise.
                Id = DeterministicGuid.Create(
                    DomainConstants.DETERMINISTIC_ID_NAMESPACE,
                    "blob-delete",
                    tenantId.ToString(),
                    attachment.AttachmentId.ToString()),
                TenantId = tenantId,
                AvailableAtUtc = now,
                ContainerName = AttachmentBlobs.ContainerName,
                BlobName = AttachmentBlobs.BlobName(tenantId, attachment.OwnerId, attachment.FileName)
            });
        }

        await DB.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
        return attachments.Count;
    }

    /// <inheritdoc />
    public async Task<int> DeleteStaleBatchAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taskIds, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return 0;
        var typedTenantId = DomainId.From<TenantId>(tenantId);
        var ids = taskIds.ToList();
        var typedIds = taskIds.Select(DomainId.From<TaskItemId>).ToList();

        // Attachments hang off a polymorphic owner, not a foreign key, so nothing cascades them.
        await DB.Set<Attachment>()
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == typedTenantId
                && a.OwnerType == AttachmentOwnerType.TaskItem
                && ids.Contains(a.OwnerId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        // Predicate restated: a task reopened between the scan and this statement must survive.
        return await StaleCandidates(cutoffUtc)
            .Where(e => e.TenantId == typedTenantId && typedIds.Contains(e.Id))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default) =>
        DB.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var transaction = await DB.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            await work(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    // Throw, not ClientWins: the rows saved here are operational (outbox, blob-delete work) and carry no
    // concurrency token, so a conflict would mean the unit of work is not what this job thinks it is.
    // `new` because RepositoryBase.SaveChangesAsync(ct) is the policy-free overload (plain
    // DbContext.SaveChangesAsync); every save on this path has to carry the Throw policy (D-032).
    public new Task<int> SaveChangesAsync(CancellationToken ct = default) =>
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

    /// <summary>Orders by the clustered key and resumes after the last row of the previous page.</summary>
    private static IQueryable<TaskItem> Keyset(IQueryable<TaskItem> source, (Guid TenantId, Guid Id)? after)
    {
        if (after is { } position)
        {
            var tenantId = DomainId.From<TenantId>(position.TenantId);
            var id = DomainId.From<TaskItemId>(position.Id);
            source = source.Where(e => e.TenantId > tenantId || (e.TenantId == tenantId && e.Id > id));
        }

        return source.AsNoTracking().OrderBy(e => e.TenantId).ThenBy(e => e.Id);
    }
}
