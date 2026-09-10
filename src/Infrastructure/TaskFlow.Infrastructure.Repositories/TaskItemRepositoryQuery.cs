using EF.Common.Contracts;
using EF.Data;
using EF.Data.Contracts;
using EF.Data.Encryption;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Runtime.CompilerServices;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Configurations;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Read-side TaskItem repository. It uses the no-tracking query DbContext and projects search
/// results server-side so list endpoints avoid hydrating child collections.
/// </summary>
public class TaskItemRepositoryQuery(TaskFlowDbContextQuery db, ColumnEncryptionKeys encryptionKeys, CursorCodec cursorCodec)
    : TaskFlowRepositoryQuery<TaskItem, TaskItemId>(db), ITaskItemRepositoryQuery
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    // Plain AsNoTracking rather than the package GetEntityAsync(tracking: false): that path uses
    // NoTrackingWithIdentityResolution, which EF Core 10 rejects when a JSON-mapped owned type
    // (RecurrencePattern) is loaded together with collection includes.
    public async Task<TaskItem?> GetTaskItemAsync(TaskItemId id, CancellationToken ct = default) =>
        await DB.Set<TaskItem>()
            .AsNoTracking()
            .AsSplitQuery()
            .Include(t => t.Category)
            .Include(t => t.Comments)
            .Include(t => t.ChecklistItems)
            .Include(t => t.TaskItemTags).ThenInclude(tt => tt.Tag)
            .Include(t => t.SubTasks)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

    /// <inheritdoc />
    public async Task<TaskItem?> FindBySecureTokenAsync(string secureDeterministic, CancellationToken ct = default)
    {
        // Equality on the keyed HMAC shadow column (IX_TaskItem_TenantId_SecureDeterministicBlindIndex); the
        // ciphertext column is randomized and can never be compared directly.
        var blindIndex = BlindIndex.Compute(secureDeterministic, encryptionKeys.BlindIndexKey);
        return await DB.Set<TaskItem>()
            .Where(t => Microsoft.EntityFrameworkCore.EF.Property<byte[]>(t, TaskItemConfiguration.SecureDeterministicBlindIndex) == blindIndex)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The cursor is <c>EF.Data.Contracts.CursorCodec</c> through <c>KeysetCursor</c> (package requests 6
    /// and 27): tenant-and-sort-mode-scoped, HMAC-signed, schema-versioned, fails closed. The ORDER BY and
    /// the resume predicate stay app-local. <c>KeysetPageAsync</c>, the pager beside the codec, takes the
    /// tie-break key as <c>Expression&lt;Func&lt;T, Guid&gt;&gt;</c>, and every TaskFlow key is an
    /// <c>IDomainId&lt;T&gt;</c> struct behind the EF.Data domain-id value converter: no Guid-typed
    /// selector over such a key translates (<c>t.Id.Value</c> reaches SQL as a member access into a
    /// converted CLR type and the whole ORDER BY is refused), so the pager cannot page this model. See the
    /// slice report; the fix is a generic tie-break key on the package side.
    /// </remarks>
    public async Task<CursorPage<TaskItemDto>> SearchTaskItemsAsync(
        TaskItemCursorSearchRequest request, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cursor = new KeysetCursor(cursorCodec, CursorScope(tenantId, request.SortMode), request.Cursor);

        // Fail closed with the app's own message. The codec rejects a tampered, wrongly-signed,
        // stale-schema or foreign-scope token; DecodePosition would throw its own ArgumentException, which
        // the global handler also answers 400, but the tamper / cross-tenant / sort-mode-mismatch contract
        // is asserted on this constant.
        if (!string.IsNullOrEmpty(request.Cursor) && !cursorCodec.TryDecode(request.Cursor, cursor.TenantKey, out _))
            throw new ArgumentException(ErrorConstants.ERROR_CURSOR_INVALID, nameof(request));

        var q = ApplyFilters(DB.Set<TaskItem>().AsNoTracking(), request.Filter);
        q = ApplyKeyset(q, request.SortMode, cursor.DecodePosition());

        // One row past the page: its existence, not a COUNT, is what says another page exists.
        var rows = await q
            .Take(request.PageSize + 1)
            .Select(TaskItemMapper.ProjectorSearch)
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        var hasMore = rows.Count > request.PageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var nextCursor = hasMore && rows.Count > 0
            ? cursor.EncodePosition(SortKey(request.SortMode, rows[^1]), rows[^1].Id!.Value)
            : null;

        return new CursorPage<TaskItemDto>(rows, nextCursor, hasMore);
    }

    /// <summary>
    /// Scope key the cursor is bound to. <c>CursorCodec</c> re-checks its tenant key on decode and fails
    /// closed on a mismatch; folding the sort mode into that key is what makes a cursor minted under one
    /// ordering unusable in another, which the codec's own payload has no field for.
    /// </summary>
    public static string CursorScope(Guid tenantId, TaskItemSortMode sortMode) =>
        string.Create(CultureInfo.InvariantCulture, $"{tenantId:N}|{(int)sortMode}");

    /// <summary>
    /// Serializes the leading sort-column value of the last row on a page. Ticks and invariant integers,
    /// matching the encoding the package codec's own sort keys use, so a later move to its pager needs no
    /// token format change; null when the column is null, which the codec models natively.
    /// </summary>
    private static string? SortKey(TaskItemSortMode sortMode, TaskItemDto last) => sortMode switch
    {
        TaskItemSortMode.IdAsc => null,
        TaskItemSortMode.DueDateAsc or TaskItemSortMode.DueDateDesc =>
            last.DueDate?.UtcTicks.ToString(CultureInfo.InvariantCulture),
        TaskItemSortMode.ModifiedDesc =>
            (last.ModifiedAtUtc ?? default).UtcTicks.ToString(CultureInfo.InvariantCulture),
        _ => ((int)last.Status).ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Reads a timestamp sort key back. Invalid text is a caller error, not a silent first page.</summary>
    private static bool TryTicks(string? sortKey, out DateTimeOffset value)
    {
        value = default;
        if (!long.TryParse(sortKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        value = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    /// <summary>Reads an integer (enum) sort key back.</summary>
    private static bool TryInt(string? sortKey, out int value) =>
        int.TryParse(sortKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <inheritdoc />
    // One GroupBy(_ => 1) with conditional SUMs: a single round trip for the whole dashboard instead of
    // one query per status. The overdue predicate matches the IsOverdue search filter exactly.
    public async Task<TaskItemSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken ct = default)
    {
        var typedTenantId = DomainId.From<TenantId>(tenantId);
        var now = DateTimeOffset.UtcNow;

        var aggregate = await DB.Set<TaskItem>()
            .AsNoTracking()
            .Where(t => t.TenantId == typedTenantId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                None = g.Count(t => t.Status == TaskItemStatus.None),
                Open = g.Count(t => t.Status == TaskItemStatus.Open),
                InProgress = g.Count(t => t.Status == TaskItemStatus.InProgress),
                Blocked = g.Count(t => t.Status == TaskItemStatus.Blocked),
                Completed = g.Count(t => t.Status == TaskItemStatus.Completed),
                Cancelled = g.Count(t => t.Status == TaskItemStatus.Cancelled),
                Overdue = g.Count(t => t.DueDate != null && t.DueDate < now && t.CompletedDate == null)
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        if (aggregate is null)
            return new TaskItemSummaryDto { GeneratedAtUtc = now };

        return new TaskItemSummaryDto
        {
            Total = aggregate.Total,
            Overdue = aggregate.Overdue,
            GeneratedAtUtc = now,
            ByStatus =
            [
                new(TaskItemStatus.None, aggregate.None),
                new(TaskItemStatus.Open, aggregate.Open),
                new(TaskItemStatus.InProgress, aggregate.InProgress),
                new(TaskItemStatus.Blocked, aggregate.Blocked),
                new(TaskItemStatus.Completed, aggregate.Completed),
                new(TaskItemStatus.Cancelled, aggregate.Cancelled)
            ]
        };
    }

    /// <inheritdoc />
    // One batch per call, ordered by the clustered key. The caller loops until a short batch, so no
    // connection is held open across a whole tenant's export.
    public async IAsyncEnumerable<TaskItemExportDto> StreamExportAsync(
        Guid tenantId, Guid? afterId, int batchSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var typedTenantId = DomainId.From<TenantId>(tenantId);
        var q = DB.Set<TaskItem>().AsNoTracking().Where(t => t.TenantId == typedTenantId);

        if (afterId is Guid resume)
        {
            var typedAfterId = DomainId.From<TaskItemId>(resume);
            q = q.Where(t => t.Id > typedAfterId);
        }

        var batch = q.OrderBy(t => t.Id)
            .Take(batchSize)
            .GetStreamProjection(ExportProjection, tracking: false);

        await foreach (var row in batch.WithCancellation(ct).ConfigureAwait(false))
            yield return row;
    }

    private static readonly Func<TaskItem, TaskItemExportDto> ExportProjection = entity => new TaskItemExportDto
    {
        Id = entity.Id.Value,
        TenantId = entity.TenantId.Value,
        Title = entity.Title,
        Description = entity.Description,
        Priority = entity.Priority,
        Status = entity.Status,
        EstimatedEffort = entity.EstimatedEffort,
        ActualEffort = entity.ActualEffort,
        StartDate = entity.StartDate,
        DueDate = entity.DueDate,
        CompletedDate = entity.CompletedDate,
        CategoryId = entity.CategoryId.HasValue ? entity.CategoryId.Value.Value : null,
        ParentTaskItemId = entity.ParentTaskItemId.HasValue ? entity.ParentTaskItemId.Value.Value : null,
        Version = entity.Version,
        CreatedAtUtc = entity.CreatedAtUtc,
        ModifiedAtUtc = entity.ModifiedAtUtc
    };

    /// <summary>Applies the TaskItem search filter shared by the cursor search.</summary>
    private static IQueryable<TaskItem> ApplyFilters(IQueryable<TaskItem> q, TaskItemSearchFilter? filter)
    {
        if (filter is null) return q;

        var searchTerm = filter.SearchTerm?.Trim();
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            // Prefix, not substring: StartsWith is an index seek on IX_TaskItem_TenantId_Title_Id, while
            // Contains forces a scan of every task in the tenant on both providers. Substring and fuzzy
            // matching is what ITaskFlowSearchService is for (AiServices:UseSearch); when that is not
            // configured, NoOpSearchService falls back to this same prefix query rather than nothing.
            q = q.Where(e => e.Title.StartsWith(searchTerm));
        }

        if (filter.Status.HasValue)
        {
            var status = filter.Status.Value;
            q = q.Where(e => e.Status == status);
        }

        if (filter.Priority.HasValue)
        {
            var priority = filter.Priority.Value;
            q = q.Where(e => e.Priority == priority);
        }

        if (filter.CategoryId.HasValue)
        {
            var categoryId = DomainId.From<CategoryId>(filter.CategoryId.Value);
            q = q.Where(e => e.CategoryId == categoryId);
        }

        if (filter.ParentTaskItemId.HasValue)
        {
            var parentTaskItemId = DomainId.From<TaskItemId>(filter.ParentTaskItemId.Value);
            q = q.Where(e => e.ParentTaskItemId == parentTaskItemId);
        }

        if (filter.TenantId.HasValue)
        {
            var tenantId = DomainId.From<TenantId>(filter.TenantId.Value);
            q = q.Where(e => e.TenantId == tenantId);
        }

        if (filter.DueBefore.HasValue)
        {
            var dueBefore = filter.DueBefore.Value;
            q = q.Where(e => e.DueDate != null && e.DueDate <= dueBefore);
        }

        if (filter.DueAfter.HasValue)
        {
            var dueAfter = filter.DueAfter.Value;
            q = q.Where(e => e.DueDate != null && e.DueDate >= dueAfter);
        }

        if (filter.IsOverdue.HasValue && filter.IsOverdue.Value)
            q = q.Where(e => e.DueDate != null && e.DueDate < DateTimeOffset.UtcNow && e.CompletedDate == null);

        return q;
    }
    /// <summary>
    /// Applies the ordering and the resume predicate for one sort mode. Both halves live in the same
    /// switch so an ORDER BY can never drift from the WHERE that is supposed to continue it, and the sort
    /// key each arm reads is the one <see cref="SortKey"/> wrote. Every arm names the index it must hit; a
    /// mode whose ORDER BY does not match an index turns this into a full scan at depth, which is exactly
    /// what cursor paging exists to avoid.
    /// </summary>
    private static IQueryable<TaskItem> ApplyKeyset(IQueryable<TaskItem> q, TaskItemSortMode sortMode, CursorPosition? after)
    {
        switch (sortMode)
        {
            case TaskItemSortMode.IdAsc:
                // IX: the clustered primary key (TenantId, Id).
                if (after is CursorPosition idPosition)
                {
                    var lastId = DomainId.From<TaskItemId>(idPosition.TieBreaker);
                    q = q.Where(e => e.Id > lastId);
                }
                return q.OrderBy(e => e.Id);

            case TaskItemSortMode.DueDateAsc:
                {
                    // IX_TaskItem_TenantId_DueDate_Id. Null DueDate sorts last through the leading
                    // (DueDate == null) key, so an unscheduled task never hides a scheduled one.
                    if (after is CursorPosition position)
                    {
                        var lastId = DomainId.From<TaskItemId>(position.TieBreaker);
                        if (position.SortKey is null)
                            q = q.Where(e => e.DueDate == null && e.Id > lastId);
                        else if (TryTicks(position.SortKey, out var lastDue))
                            q = q.Where(e => e.DueDate == null
                                || e.DueDate > lastDue
                                || (e.DueDate == lastDue && e.Id > lastId));
                    }
                    return q.OrderBy(e => e.DueDate == null).ThenBy(e => e.DueDate).ThenBy(e => e.Id);
                }

            case TaskItemSortMode.DueDateDesc:
                {
                    // IX_TaskItem_TenantId_DueDate_Id, scanned backwards; nulls still sort last.
                    if (after is CursorPosition position)
                    {
                        var lastId = DomainId.From<TaskItemId>(position.TieBreaker);
                        if (position.SortKey is null)
                            q = q.Where(e => e.DueDate == null && e.Id > lastId);
                        else if (TryTicks(position.SortKey, out var lastDue))
                            q = q.Where(e => e.DueDate == null
                                || e.DueDate < lastDue
                                || (e.DueDate == lastDue && e.Id > lastId));
                    }
                    return q.OrderBy(e => e.DueDate == null).ThenByDescending(e => e.DueDate).ThenBy(e => e.Id);
                }

            case TaskItemSortMode.ModifiedDesc:
                {
                    // IX_TaskItem_TenantId_ModifiedAtUtc_Id, scanned backwards.
                    if (after is CursorPosition position && TryTicks(position.SortKey, out var lastModified))
                    {
                        var lastId = DomainId.From<TaskItemId>(position.TieBreaker);
                        q = q.Where(e => e.ModifiedAtUtc < lastModified
                            || (e.ModifiedAtUtc == lastModified && e.Id > lastId));
                    }
                    return q.OrderByDescending(e => e.ModifiedAtUtc).ThenBy(e => e.Id);
                }

            case TaskItemSortMode.StatusThenId:
            default:
                {
                    // IX_TaskItem_TenantId_Status_Id.
                    if (after is CursorPosition position && TryInt(position.SortKey, out var lastStatusValue))
                    {
                        var lastStatus = (TaskItemStatus)lastStatusValue;
                        var lastId = DomainId.From<TaskItemId>(position.TieBreaker);
                        q = q.Where(e => e.Status > lastStatus
                            || (e.Status == lastStatus && e.Id > lastId));
                    }
                    return q.OrderBy(e => e.Status).ThenBy(e => e.Id);
                }
        }
    }
}
