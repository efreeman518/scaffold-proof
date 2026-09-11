using EF.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.ReadModel;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Relational TaskView read model (D-038): the portable-lane arm of <see cref="ITaskViewRepository"/>,
/// semantics matched to <c>CosmosTaskViewRepository</c> so the producers (<c>TaskViewProjectionService</c>,
/// <c>TaskProjectionConsumer</c>) and <c>TaskViewEndpoints</c> cannot tell the two apart:
/// <list type="bullet">
/// <item>tenant is an explicit argument, never an ambient filter - the relational stand-in for the partition key;</item>
/// <item>the continuation token is opaque to the caller (<see cref="TaskViewKeysetToken"/>);</item>
/// <item>counter patches are one server-side statement, so two concurrent delta events cannot overwrite
/// each other the way a read-modify-write would;</item>
/// <item>a missing row is a no-op for patch and delete, because the create projection rebuilds counters
/// from the source.</item>
/// </list>
/// D-027 split: projection writes and counter patches run on the Trxn context (read-your-writes for the
/// consumer that just wrote), while endpoint gets and list pages read the Query context, which is the
/// read-replica connection - a read model is eventually consistent by construction, so replica lag adds
/// nothing the projection delay has not already added.
/// </summary>
public sealed class RelationalTaskViewRepository(TaskFlowDbContextTrxn write, TaskFlowDbContextQuery read)
    : RepositoryBase<TaskFlowDbContextTrxn, string, Guid?>(write), ITaskViewRepository
{
    // Counter names as the producer spells them: TaskViewProjectionService keys the delta dictionary on the
    // Cosmos document's JSON property names, and that contract is not this arm's to change.
    private const string CommentCountField = "commentCount";
    private const string AttachmentCountField = "attachmentCount";
    private const string ChecklistTotalField = "checklistTotal";
    private const string ChecklistCompletedField = "checklistCompleted";

    /// <inheritdoc />
    public Task UpsertAsync(TaskViewDto taskView, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(taskView);

        // D-028: MERGE on SQL Server, ON CONFLICT DO UPDATE on PostgreSQL, keyed on the primary key. Every
        // non-key column is replaced, which is what Cosmos UpsertItem does with the whole document, so the
        // whenMatched projection has to name all of them - the package's default (null) is DO NOTHING, which
        // would silently freeze an already-projected row at its first version.
        return UpsertAsync(
            MapToRecord(taskView),
            e => new { e.TenantId, e.Id },
            (existing, proposed) => new TaskViewRecord
            {
                Title = proposed.Title,
                Status = proposed.Status,
                Priority = proposed.Priority,
                CategoryName = proposed.CategoryName,
                StartDate = proposed.StartDate,
                DueDate = proposed.DueDate,
                CompletedDate = proposed.CompletedDate,
                IsOverdue = proposed.IsOverdue,
                CommentCount = proposed.CommentCount,
                ChecklistTotal = proposed.ChecklistTotal,
                ChecklistCompleted = proposed.ChecklistCompleted,
                AttachmentCount = proposed.AttachmentCount,
                SubTaskCount = proposed.SubTaskCount,
                CreatedUtc = proposed.CreatedUtc,
                LastModifiedUtc = proposed.LastModifiedUtc,
                Document = proposed.Document
            },
            ct);
    }

    /// <inheritdoc />
    public async Task<TaskViewDto?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        var record = await read.TaskViews.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == id, ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        return record is null ? null : MapToDto(record);
    }

    /// <inheritdoc />
    public async Task<TaskViewPage> QueryByTenantAsync(
        string tenantId, int pageSize = 20, string? continuationToken = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var query = read.TaskViews.AsNoTracking().Where(e => e.TenantId == tenantId);

        if (!string.IsNullOrEmpty(continuationToken))
        {
            var after = TaskViewKeysetToken.Decode(continuationToken, tenantId);
            // Keyset, not OFFSET: a row inserted between two requests shifts an offset and duplicates or
            // skips a row. The tie-break on Id is what makes rows sharing a timestamp a total order.
            query = query.Where(e => e.LastModifiedUtc < after.LastModifiedUtc
                || (e.LastModifiedUtc == after.LastModifiedUtc && string.Compare(e.Id, after.Id) < 0));
        }

        // One row past the page: its existence, not a count query, is what says another page exists.
        var rows = await query
            .OrderByDescending(e => e.LastModifiedUtc).ThenByDescending(e => e.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var token = hasMore
            ? TaskViewKeysetToken.Encode(tenantId, rows[^1].LastModifiedUtc, rows[^1].Id)
            : null;

        return new TaskViewPage([.. rows.Select(MapToDto)], token);
    }

    /// <inheritdoc />
    public async Task PatchCountersAsync(string id, string tenantId,
        IReadOnlyDictionary<string, int> increments, DateTimeOffset lastModifiedUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(increments);
        if (increments.Count == 0) return;

        var comments = Delta(increments, CommentCountField);
        var attachments = Delta(increments, AttachmentCountField);
        var checklistTotal = Delta(increments, ChecklistTotalField);
        var checklistCompleted = Delta(increments, ChecklistCompletedField);
        if (comments == 0 && attachments == 0 && checklistTotal == 0 && checklistCompleted == 0) return;

        // One statement, deltas applied by the database (`SET x = x + @d`), so concurrent events for the
        // same row add up instead of the later one overwriting a value the earlier one read. EF Core 10's
        // statement-bodied setter lambda is what lets the zero deltas stay out of the UPDATE entirely.
        // Affected == 0 means the row is not projected yet: a no-op, matching the Cosmos 404 arm.
        await DB.TaskViews
            .Where(e => e.TenantId == tenantId && e.Id == id)
            .ExecuteUpdateAsync(s =>
            {
                if (comments != 0) s.SetProperty(e => e.CommentCount, e => e.CommentCount + comments);
                if (attachments != 0) s.SetProperty(e => e.AttachmentCount, e => e.AttachmentCount + attachments);
                if (checklistTotal != 0) s.SetProperty(e => e.ChecklistTotal, e => e.ChecklistTotal + checklistTotal);
                if (checklistCompleted != 0) s.SetProperty(e => e.ChecklistCompleted, e => e.ChecklistCompleted + checklistCompleted);
                s.SetProperty(e => e.LastModifiedUtc, lastModifiedUtc);
            }, ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, string tenantId, CancellationToken ct = default) =>
        // Zero rows deleted is success: the document is already gone, the Cosmos 404 arm's semantics.
        DB.TaskViews.Where(e => e.TenantId == tenantId && e.Id == id).ExecuteDeleteAsync(ct);

    private static int Delta(IReadOnlyDictionary<string, int> increments, string field) =>
        increments.TryGetValue(field, out var delta) ? delta : 0;

    private static TaskViewRecord MapToRecord(TaskViewDto dto) => new()
    {
        TenantId = dto.TenantId,
        Id = dto.Id,
        Title = dto.Title,
        Status = dto.Status,
        Priority = dto.Priority,
        CategoryName = dto.CategoryName,
        StartDate = dto.StartDate,
        DueDate = dto.DueDate,
        CompletedDate = dto.CompletedDate,
        IsOverdue = dto.IsOverdue,
        CommentCount = dto.CommentCount,
        ChecklistTotal = dto.ChecklistTotal,
        ChecklistCompleted = dto.ChecklistCompleted,
        AttachmentCount = dto.AttachmentCount,
        SubTaskCount = dto.SubTaskCount,
        CreatedUtc = dto.CreatedUtc,
        LastModifiedUtc = dto.LastModifiedUtc,
        Document = JsonSerializer.Serialize(
            new TaskViewBody(dto.Description, dto.Tags), TaskViewBodyJsonContext.Default.TaskViewBody)
    };

    private static TaskViewDto MapToDto(TaskViewRecord record)
    {
        var body = JsonSerializer.Deserialize(record.Document, TaskViewBodyJsonContext.Default.TaskViewBody);

        return new TaskViewDto
        {
            Id = record.Id,
            TenantId = record.TenantId,
            Title = record.Title,
            Description = body?.Description,
            Status = record.Status,
            Priority = record.Priority,
            CategoryName = record.CategoryName,
            StartDate = record.StartDate,
            DueDate = record.DueDate,
            CompletedDate = record.CompletedDate,
            IsOverdue = record.IsOverdue,
            Tags = body?.Tags ?? [],
            CommentCount = record.CommentCount,
            ChecklistTotal = record.ChecklistTotal,
            ChecklistCompleted = record.ChecklistCompleted,
            AttachmentCount = record.AttachmentCount,
            SubTaskCount = record.SubTaskCount,
            LastModifiedUtc = record.LastModifiedUtc,
            CreatedUtc = record.CreatedUtc
        };
    }
}
