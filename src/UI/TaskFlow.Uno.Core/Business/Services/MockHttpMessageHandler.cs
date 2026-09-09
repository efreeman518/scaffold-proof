using System.Net;
using System.Text.Json;
using TaskFlow.Uno.Core.Client;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>
/// Stateful mock handler - CRUD operations modify in-memory collections.
/// Thread-safe via lock; data persists for the app session lifetime.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly object _lock = new();

    private readonly List<CategoryDto> _categories =
    [
        new() { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Version = 1, Name = "Development", Description = "Dev tasks", SortOrder = 1, IsActive = true },
        new() { Id = Guid.Parse("11111111-1111-1111-1111-222222222222"), Version = 1, Name = "Documentation", Description = "Docs tasks", SortOrder = 2, IsActive = true }
    ];

    private readonly List<TagDto> _tags =
    [
        new() { Id = Guid.Parse("22222222-2222-2222-2222-111111111111"), Version = 1, Name = "frontend", Color = "#3B82F6" },
        new() { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Version = 1, Name = "backend", Color = "#10B981" }
    ];

    private readonly List<CommentDto> _comments =
    [
        new() { Id = Guid.Parse("44444444-4444-4444-4444-111111111111"), Version = 1, Body = "Looking good so far!", TaskItemId = Guid.Parse("33333333-3333-3333-3333-111111111111") }
    ];

    private readonly List<ChecklistItemDto> _checklistItems =
    [
        new() { Id = Guid.Parse("55555555-5555-5555-5555-111111111111"), Version = 1, Title = "Design mockups", IsCompleted = true, SortOrder = 1, TaskItemId = Guid.Parse("33333333-3333-3333-3333-111111111111") },
        new() { Id = Guid.Parse("55555555-5555-5555-5555-222222222222"), Version = 1, Title = "Implement XAML", IsCompleted = false, SortOrder = 2, TaskItemId = Guid.Parse("33333333-3333-3333-3333-111111111111") }
    ];

    private readonly List<AttachmentDto> _attachments =
    [
        new() { Id = Guid.Parse("66666666-6666-6666-6666-111111111111"), Version = 1, FileName = "design.pdf", ContentType = "application/pdf",
                FileSizeBytes = 4096, StorageUri = "https://storage.example.com/design.pdf",
                OwnerType = "TaskItem", OwnerId = Guid.Parse("33333333-3333-3333-3333-111111111111") }
    ];

    private readonly List<TaskItemDto> _tasks = CreateSeedTasks();

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    private static List<TaskItemDto> CreateSeedTasks()
    {
        var now = DateTimeOffset.UtcNow;

        return
        [
            new() { Id = Guid.Parse("33333333-3333-3333-3333-111111111111"), Version = 1, Title = "Build dashboard UI", Description = "Create the main dashboard page with stats and recent activity",
                Priority = "High", Status = "InProgress", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                StartDate = now.AddDays(-5), DueDate = now.AddDays(2) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-222222222222"), Version = 1, Title = "Fix login validation", Description = "Users can submit empty passwords",
                Priority = "Critical", Status = "Open", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                DueDate = now.AddDays(-1) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Version = 1, Title = "Write documentation", Description = "Update API docs for v2",
                Priority = "Low", Status = "Completed", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-222222222222"), CategoryName = "Documentation",
                CompletedDate = now.AddDays(-2) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-444444444444"), Version = 1, Title = "Review sprint backlog", Description = "Re-rank incoming product requests for the next planning session",
                Priority = "Medium", Status = "Open", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                StartDate = now.AddDays(-4), DueDate = now.AddDays(4) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-555555555555"), Version = 1, Title = "Refactor API client", Description = "Simplify retry and error handling in the generated client wrapper",
                Priority = "High", Status = "InProgress", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                StartDate = now.AddDays(-3), DueDate = now.AddDays(5) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-666666666666"), Version = 1, Title = "Design empty states", Description = "Create polished guidance for empty dashboard and list views",
                Priority = "Medium", Status = "Open", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                StartDate = now.AddDays(-2), DueDate = now.AddDays(6) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-777777777777"), Version = 1, Title = "Prepare onboarding guide", Description = "Document the first-run flow for new teammates",
                Priority = "Low", Status = "Open", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-222222222222"), CategoryName = "Documentation",
                DueDate = now.AddDays(7) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-888888888888"), Version = 1, Title = "Publish release checklist", Description = "Capture deployment sign-off steps before the next cut",
                Priority = "High", Status = "Blocked", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-222222222222"), CategoryName = "Documentation",
                StartDate = now.AddDays(-1), DueDate = now.AddDays(1) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-999999999999"), Version = 1, Title = "Audit role permissions", Description = "Verify admin-only actions are hidden from standard contributors",
                Priority = "Critical", Status = "Open", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                DueDate = now.AddDays(3) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-aaaaaaaaaaaa"), Version = 1, Title = "Draft changelog notes", Description = "Write a concise summary of fixes and improvements for the release notes",
                Priority = "Low", Status = "Completed", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-222222222222"), CategoryName = "Documentation",
                CompletedDate = now.AddDays(-1) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-bbbbbbbbbbbb"), Version = 1, Title = "Triage regression bugs", Description = "Review the latest failures and assign owners before standup",
                Priority = "High", Status = "InProgress", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                StartDate = now.AddDays(-6), DueDate = now },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-cccccccccccc"), Version = 1, Title = "Document keyboard shortcuts", Description = "Add quick-reference guidance for the new command palette",
                Priority = "Low", Status = "Open", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-222222222222"), CategoryName = "Documentation",
                DueDate = now.AddDays(9) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-dddddddddddd"), Version = 1, Title = "Validate export format", Description = "Confirm CSV exports handle multi-line descriptions and missing dates",
                Priority = "Medium", Status = "Cancelled", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                DueDate = now.AddDays(8) },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-eeeeeeeeeeee"), Version = 1, Title = "Plan analytics backlog", Description = "Define the first reporting tasks for the metrics roadmap",
                Priority = "Medium", Status = "Open", Features = "None",
                CategoryId = Guid.Parse("11111111-1111-1111-1111-111111111111"), CategoryName = "Development",
                DueDate = now.AddDays(10) }
        ];
    }

    /// <summary>Processes HTTP requests through mock HTTP message handler and applies its cross-cutting policy.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.PathAndQuery ?? "";
        var method = request.Method.Method;

        lock (_lock)
        {
            // --- TASK ITEMS ---
            if (path.Contains("/task-items/search") && method == "POST")
                return SearchTaskItemsCursor(request);
            if (path.Contains("/task-items/summary") && method == "GET")
                return TaskItemsSummary();
            if (path.Contains("/task-items") && method == "POST" && !path.Contains("/search"))
                return CreateEntity(_tasks, request, t => { t.Id ??= Guid.CreateVersion7(); t.Version = 1; t.CategoryName ??= FindCategoryName(t.CategoryId); }, prepend: true);

            // Nested child routes - matched before the bare /task-items/{id} routes below.
            if (TryExtractNestedId(path, "/task-items/", "/comments", out var commentTaskId) && method == "POST")
                return AddComment(commentTaskId, request);
            if (TryExtractNestedId(path, "/task-items/", "/comments/", out var _, out var commentId) && method == "DELETE")
                return RemoveComment(commentId);
            if (TryExtractNestedId(path, "/task-items/", "/checklist-items", out var checklistTaskId) && method == "POST")
                return AddChecklistItem(checklistTaskId, request);
            if (TryExtractNestedId(path, "/task-items/", "/checklist-items/", out var _, out var checklistItemId))
            {
                if (method == "PUT") return UpdateChecklistItem(checklistItemId, request);
                if (method == "DELETE") return RemoveChecklistItem(checklistItemId);
            }

            if (TryExtractId(path, "/task-items/", out var taskId))
            {
                if (method == "GET") return GetById(_tasks, taskId);
                if (method == "PUT") return UpdateEntity(_tasks, taskId, request, t => { t.CategoryName ??= FindCategoryName(t.CategoryId); BumpVersion(t); });
                if (method == "DELETE") return DeleteTask(taskId);
            }

            // --- TASK METADATA ---
            if (path.Contains("/task-metadata") && method == "GET")
                return TaskMetadata();

            // --- CATEGORIES ---
            if (path.Contains("/categories/search") && method == "POST")
                return SearchCategories(request);
            if (path.Contains("/categories") && method == "POST" && !path.Contains("/search"))
                return CreateEntity(_categories, request, c => { c.Id ??= Guid.CreateVersion7(); c.Version = 1; c.IsActive ??= true; });
            if (TryExtractId(path, "/categories/", out var catId))
            {
                if (method == "GET") return GetById(_categories, catId);
                if (method == "PUT") return UpdateEntity(_categories, catId, request, BumpVersion);
                if (method == "DELETE") return DeleteEntity(_categories, catId);
            }

            // --- TAGS ---
            if (path.Contains("/tags/search") && method == "POST")
                return SearchTags(request);
            if (path.Contains("/tags") && method == "POST" && !path.Contains("/search"))
                return CreateEntity(_tags, request, t => { t.Id ??= Guid.CreateVersion7(); t.Version = 1; });
            if (TryExtractId(path, "/tags/", out var tagId))
            {
                if (method == "GET") return GetById(_tags, tagId);
                if (method == "PUT") return UpdateEntity(_tags, tagId, request, BumpVersion);
                if (method == "DELETE") return DeleteEntity(_tags, tagId);
            }

            // --- ATTACHMENTS ---
            if (path.Contains("/attachments/search") && method == "POST")
                return SearchAttachments(request);
            if (path.Contains("/attachments") && method == "POST" && !path.Contains("/search"))
                return CreateEntity(_attachments, request, a => { a.Id ??= Guid.CreateVersion7(); a.Version = 1; });
            if (TryExtractId(path, "/attachments/", out var attachId))
            {
                if (method == "GET") return GetById(_attachments, attachId);
                if (method == "DELETE") return DeleteEntity(_attachments, attachId);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // --- Helpers ---

    private string? FindCategoryName(Guid? categoryId) =>
        categoryId.HasValue ? _categories.FirstOrDefault(c => c.Id == categoryId)?.Name : null;

    /// <summary>Bumps the row Version by one, mimicking the server's optimistic-concurrency bump on write.</summary>
    private static void BumpVersion<T>(T item) where T : class
    {
        switch (item)
        {
            case TaskItemDto t: t.Version = (t.Version ?? 0) + 1; break;
            case CategoryDto c: c.Version = (c.Version ?? 0) + 1; break;
            case TagDto t: t.Version = (t.Version ?? 0) + 1; break;
            case CommentDto c: c.Version = (c.Version ?? 0) + 1; break;
            case ChecklistItemDto c: c.Version = (c.Version ?? 0) + 1; break;
            case AttachmentDto a: a.Version = (a.Version ?? 0) + 1; break;
        }
    }

    /// <summary>Provides the try extract ID operation for mock HTTP message handler.</summary>
    private static bool TryExtractId(string path, string prefix, out Guid id)
    {
        id = Guid.Empty;
        var idx = path.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var segment = path[(idx + prefix.Length)..].TrimEnd('/').Split('?')[0];
        return Guid.TryParse(segment, out id);
    }

    /// <summary>Matches "{prefix}{guid}{suffix}" with nothing after (e.g. /task-items/{id}/comments).</summary>
    private static bool TryExtractNestedId(string path, string prefix, string suffix, out Guid parentId)
    {
        parentId = Guid.Empty;
        var idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var afterPrefix = path[(idx + prefix.Length)..];
        var suffixIdx = afterPrefix.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (suffixIdx < 0) return false;
        var idSegment = afterPrefix[..suffixIdx];
        if (!Guid.TryParse(idSegment, out parentId)) return false;
        // Reject deeper paths (e.g. .../comments/{id}) which the two-id overload below handles.
        return afterPrefix[(suffixIdx + suffix.Length)..].TrimEnd('/').Split('?')[0].Length == 0;
    }

    /// <summary>Matches "{prefix}{guid}{suffix}{guid}" (e.g. /task-items/{id}/comments/{commentId}).</summary>
    private static bool TryExtractNestedId(string path, string prefix, string suffix, out Guid parentId, out Guid childId)
    {
        parentId = Guid.Empty;
        childId = Guid.Empty;
        var idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var afterPrefix = path[(idx + prefix.Length)..];
        var suffixIdx = afterPrefix.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (suffixIdx < 0) return false;
        var idSegment = afterPrefix[..suffixIdx];
        if (!Guid.TryParse(idSegment, out parentId)) return false;
        var childSegment = afterPrefix[(suffixIdx + suffix.Length)..].TrimEnd('/').Split('?')[0];
        return Guid.TryParse(childSegment, out childId);
    }

    /// <summary>Searches tasks with keyset (cursor) paging - the cursor here is just the last-seen index, base64'd.</summary>
    private HttpResponseMessage SearchTaskItemsCursor(HttpRequestMessage request)
    {
        var searchRequest = ReadBody<TaskItemCursorSearchRequest>(request) ?? new TaskItemCursorSearchRequest();
        var filter = searchRequest.Filter;

        var ordered = _tasks
            .Where(task => string.IsNullOrWhiteSpace(filter.SearchTerm)
                || Contains(task.Title, filter.SearchTerm)
                || Contains(task.Description, filter.SearchTerm)
                || Contains(task.CategoryName, filter.SearchTerm))
            .Where(task => string.IsNullOrWhiteSpace(filter.Status)
                || string.Equals(task.Status, filter.Status, StringComparison.OrdinalIgnoreCase))
            .Where(task => string.IsNullOrWhiteSpace(filter.Priority)
                || string.Equals(task.Priority, filter.Priority, StringComparison.OrdinalIgnoreCase))
            .Where(task => filter.CategoryId is null || task.CategoryId == filter.CategoryId)
            .OrderBy(task => task.Id)
            .ToList();

        var start = searchRequest.Cursor is { Length: > 0 } c && int.TryParse(c, out var parsed) ? parsed : 0;
        var pageSize = Math.Max(1, searchRequest.PageSize);
        var page = ordered.Skip(start).Take(pageSize).ToList();
        var hasMore = start + page.Count < ordered.Count;

        return JsonResponse(new CursorPage<TaskItemDto>
        {
            Data = page,
            NextCursor = hasMore ? (start + page.Count).ToString() : null,
            HasMore = hasMore
        });
    }

    private HttpResponseMessage TaskItemsSummary()
    {
        var byStatus = _tasks.GroupBy(t => t.Status ?? "Open")
            .Select(g => new TaskItemStatusCountDto { Status = g.Key, Count = g.Count() })
            .ToList();
        var overdue = _tasks.Count(t => t.DueDate is { } due && due < DateTimeOffset.UtcNow
            && t.Status is not ("Completed" or "Cancelled"));

        return JsonResponse(new TaskItemSummaryDto
        {
            ByStatus = byStatus,
            Overdue = overdue,
            Total = _tasks.Count,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private HttpResponseMessage TaskMetadata() => JsonResponse(new TaskMetadataDto
    {
        Categories = [.. _categories],
        Tags = [.. _tags],
        GeneratedAtUtc = DateTimeOffset.UtcNow
    });

    private HttpResponseMessage AddComment(Guid taskId, HttpRequestMessage request)
    {
        var body = ReadBody<DefaultRequest<CommentDto>>(request);
        if (body?.Item is null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        body.Item.Id = Guid.CreateVersion7();
        body.Item.Version = 1;
        body.Item.TaskItemId = taskId;
        _comments.Insert(0, body.Item);
        return JsonResponse(new DefaultResponse<CommentDto> { Item = body.Item }, HttpStatusCode.Created);
    }

    private HttpResponseMessage RemoveComment(Guid commentId)
    {
        _comments.RemoveAll(c => c.Id == commentId);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage AddChecklistItem(Guid taskId, HttpRequestMessage request)
    {
        var body = ReadBody<DefaultRequest<ChecklistItemDto>>(request);
        if (body?.Item is null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        body.Item.Id = Guid.CreateVersion7();
        body.Item.Version = 1;
        body.Item.TaskItemId = taskId;
        body.Item.SortOrder ??= _checklistItems.Where(i => i.TaskItemId == taskId).Select(i => i.SortOrder ?? 0).DefaultIfEmpty().Max() + 1;
        _checklistItems.Add(body.Item);
        return JsonResponse(new DefaultResponse<ChecklistItemDto> { Item = body.Item }, HttpStatusCode.Created);
    }

    private HttpResponseMessage UpdateChecklistItem(Guid checklistItemId, HttpRequestMessage request)
    {
        var body = ReadBody<DefaultRequest<ChecklistItemDto>>(request);
        if (body?.Item is null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        var idx = _checklistItems.FindIndex(i => i.Id == checklistItemId);
        if (idx < 0) return new HttpResponseMessage(HttpStatusCode.NotFound);
        body.Item.Id = checklistItemId;
        body.Item.Version = (_checklistItems[idx].Version ?? 0) + 1;
        _checklistItems[idx] = body.Item;
        return JsonResponse(new DefaultResponse<ChecklistItemDto> { Item = body.Item });
    }

    private HttpResponseMessage RemoveChecklistItem(Guid checklistItemId)
    {
        _checklistItems.RemoveAll(i => i.Id == checklistItemId);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    /// <summary>Searches search categories and returns filtered results for callers.</summary>
    private HttpResponseMessage SearchCategories(HttpRequestMessage request)
    {
        var searchRequest = ReadBody<SearchRequest<CategorySearchFilter>>(request) ?? new SearchRequest<CategorySearchFilter>();
        var filter = searchRequest.Filter ?? new CategorySearchFilter();

        var filtered = _categories
            .Where(category => string.IsNullOrWhiteSpace(filter.SearchTerm)
                || Contains(category.Name, filter.SearchTerm)
                || Contains(category.Description, filter.SearchTerm))
            .Where(category => filter.IsActive is null || category.IsActive == filter.IsActive)
            .Where(category => filter.ParentCategoryId is null || category.ParentCategoryId == filter.ParentCategoryId)
            .OrderBy(category => category.SortOrder ?? 0)
            .ThenBy(category => category.Name)
            .ToList();

        return PagedResult(filtered, searchRequest.PageIndex, searchRequest.PageSize);
    }

    /// <summary>Searches search tags and returns filtered results for callers.</summary>
    private HttpResponseMessage SearchTags(HttpRequestMessage request)
    {
        var searchRequest = ReadBody<SearchRequest<TagSearchFilter>>(request) ?? new SearchRequest<TagSearchFilter>();
        var filter = searchRequest.Filter ?? new TagSearchFilter();

        var filtered = _tags
            .Where(tag => string.IsNullOrWhiteSpace(filter.SearchTerm) || Contains(tag.Name, filter.SearchTerm))
            .OrderBy(tag => tag.Name)
            .ToList();

        return PagedResult(filtered, searchRequest.PageIndex, searchRequest.PageSize);
    }

    /// <summary>Searches search attachments and returns filtered results for callers.</summary>
    private HttpResponseMessage SearchAttachments(HttpRequestMessage request)
    {
        var searchRequest = ReadBody<SearchRequest<AttachmentSearchFilter>>(request) ?? new SearchRequest<AttachmentSearchFilter>();
        var filter = searchRequest.Filter ?? new AttachmentSearchFilter();

        var filtered = _attachments
            .Where(attachment => filter.OwnerId is null || attachment.OwnerId == filter.OwnerId)
            .Where(attachment => string.IsNullOrWhiteSpace(filter.OwnerType)
                || string.Equals(attachment.OwnerType, filter.OwnerType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return PagedResult(filtered, searchRequest.PageIndex, searchRequest.PageSize);
    }

    /// <summary>Provides the paged result operation for mock HTTP message handler.</summary>
    private static HttpResponseMessage PagedResult<T>(IReadOnlyList<T> items, int pageIndex, int pageSize) where T : class
    {
        var normalizedPageIndex = Math.Max(1, pageIndex);
        var normalizedPageSize = Math.Max(1, pageSize);
        var skipped = (normalizedPageIndex - 1) * normalizedPageSize;

        return JsonResponse(new PagedResponse<T>
        {
            Data = items.Skip(skipped).Take(normalizedPageSize).ToList(),
            Total = items.Count,
            PageIndex = normalizedPageIndex,
            PageSize = normalizedPageSize
        });
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static HttpResponseMessage GetById<T>(List<T> list, Guid id) where T : class
    {
        var item = list.FirstOrDefault(i => GetId(i) == id);
        return item is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : JsonResponse(new DefaultResponse<T> { Item = item });
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    private static HttpResponseMessage CreateEntity<T>(List<T> list, HttpRequestMessage request, Action<T>? postProcess = null, bool prepend = false) where T : class
    {
        var body = ReadBody<DefaultRequest<T>>(request);
        if (body?.Item is null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        postProcess?.Invoke(body.Item);
        if (prepend)
        {
            list.Insert(0, body.Item);
        }
        else
        {
            list.Add(body.Item);
        }
        return JsonResponse(new DefaultResponse<T> { Item = body.Item }, HttpStatusCode.Created);
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    private static HttpResponseMessage UpdateEntity<T>(List<T> list, Guid id, HttpRequestMessage request, Action<T>? postProcess = null) where T : class
    {
        var body = ReadBody<DefaultRequest<T>>(request);
        if (body?.Item is null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        var idx = list.FindIndex(i => GetId(i) == id);
        if (idx < 0) return new HttpResponseMessage(HttpStatusCode.NotFound);
        SetId(body.Item, id);
        postProcess?.Invoke(body.Item);
        list[idx] = body.Item;
        return JsonResponse(new DefaultResponse<T> { Item = body.Item });
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    private static HttpResponseMessage DeleteEntity<T>(List<T> list, Guid id) where T : class
    {
        list.RemoveAll(i => GetId(i) == id);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    private HttpResponseMessage DeleteTask(Guid taskId)
    {
        _tasks.RemoveAll(task => task.Id == taskId);
        _comments.RemoveAll(comment => comment.TaskItemId == taskId);
        _checklistItems.RemoveAll(item => item.TaskItemId == taskId);
        _attachments.RemoveAll(attachment => attachment.OwnerType == "TaskItem" && attachment.OwnerId == taskId);

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    /// <summary>Provides the contains operation for mock HTTP message handler.</summary>
    private static bool Contains(string? source, string? value) =>
        !string.IsNullOrWhiteSpace(source)
        && !string.IsNullOrWhiteSpace(value)
        && source.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static Guid? GetId<T>(T item) where T : class =>
        item switch
        {
            TaskItemDto t => t.Id,
            CategoryDto c => c.Id,
            TagDto t => t.Id,
            CommentDto c => c.Id,
            ChecklistItemDto c => c.Id,
            AttachmentDto a => a.Id,
            _ => null
        };

    /// <summary>Provides the set ID operation for mock HTTP message handler.</summary>
    private static void SetId<T>(T item, Guid id) where T : class
    {
        switch (item)
        {
            case TaskItemDto t: t.Id = id; break;
            case CategoryDto c: c.Id = id; break;
            case TagDto t: t.Id = id; break;
            case CommentDto c: c.Id = id; break;
            case ChecklistItemDto c: c.Id = id; break;
            case AttachmentDto a: a.Id = id; break;
        }
    }

    /// <summary>Reads body from the configured source.</summary>
    private static T? ReadBody<T>(HttpRequestMessage request)
    {
        if (request.Content is null) return default;
        var json = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonSerializer.Deserialize(json, TaskFlowApiJson.TypeInfo<T>());
    }

    /// <summary>Provides the JSON response operation for mock HTTP message handler.</summary>
    private static HttpResponseMessage JsonResponse<T>(T data, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(data, TaskFlowApiJson.TypeInfo<T>()) };
}
