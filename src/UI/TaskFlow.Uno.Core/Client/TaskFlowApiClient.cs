using System.Text.Json.Serialization;

// Hand-authored client, not Refit: referencing the shared TaskFlow.Application.Models DTOs (or the
// generated TaskFlow.ApiClient, which depends on them) from this Uno WASM head fails the build with
// CS0118 "'Application' is a namespace but is used like a type" - reproduced with nothing more than a
// bare ProjectReference to TaskFlow.Application.Models, independent of Refit. The Uno/WinUI XAML
// compiler resolves the unqualified `Application` in App.xaml.cs (`class App : Application`) against
// the `TaskFlow.Application` namespace segment instead of Microsoft.UI.Xaml.Application once any
// referenced assembly exposes that namespace path. Renaming the shared project's namespace would
// ripple through every other consumer, so this client keeps its own DTOs (kept in sync by hand) and
// its own request builders instead. See docs/plans/client-generation.md.

namespace TaskFlow.Uno.Core.Client;

/// <summary>
/// Thin typed wrapper over HttpClient for Uno services. It intentionally models the API
/// route tree instead of business behavior; mapping and user notifications live one layer up.
/// </summary>
public class TaskFlowApiClient
{
    private readonly HttpClient _httpClient;

    /// <summary>Initializes task flow API client with required dependencies and default state.</summary>
    public TaskFlowApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public ApiRequestBuilder Api => new(_httpClient);
}

/// <summary>
/// Entry point for versioned API resources. Every builder below owns only transport concerns:
/// route, envelope, JSON shape, and status-code enforcement.
/// </summary>
public class ApiRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes API request builder with required dependencies and default state.</summary>
    public ApiRequestBuilder(HttpClient http) => _http = http;

    public TaskItemsRequestBuilder TaskItems => new(_http);
    public CategoriesRequestBuilder Categories => new(_http);
    public TagsRequestBuilder Tags => new(_http);
    public AttachmentsRequestBuilder Attachments => new(_http);
    public TaskMetadataRequestBuilder TaskMetadata => new(_http);
}

#region DTOs (transport layer - matches API contract)

/// <summary>Wraps a DTO for POST/PUT requests. Matches API DefaultRequest&lt;T&gt;.</summary>
public class DefaultRequest<T>
{
    [JsonPropertyName("item")]
    public T Item { get; set; } = default!;
}

/// <summary>Wraps a DTO in GET/POST/PUT responses. Matches API DefaultResponse&lt;T&gt;.</summary>
public class DefaultResponse<T>
{
    [JsonPropertyName("item")]
    public T? Item { get; set; }
}

/// <summary>Carries task item data across API, application, and UI boundaries.</summary>
public class TaskItemDto
{
    public Guid? Id { get; set; }

    /// <summary>App-managed optimistic concurrency token (D-021), echoed back as If-Match on writes.</summary>
    public long? Version { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Priority { get; set; }
    public string? Status { get; set; }
    public string? Features { get; set; }
    public decimal? EstimatedEffort { get; set; }
    public decimal? ActualEffort { get; set; }
    public DateTimeOffset? CompletedDate { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? ParentTaskItemId { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public int? RecurrenceInterval { get; set; }
    public string? RecurrenceFrequency { get; set; }
    public DateTimeOffset? RecurrenceEndDate { get; set; }
    public string? CategoryName { get; set; }
    public List<CommentDto>? Comments { get; set; }
    public List<ChecklistItemDto>? ChecklistItems { get; set; }
    public List<TagDto>? Tags { get; set; }
    public List<TaskItemDto>? SubTasks { get; set; }
}

/// <summary>Carries category data across API, application, and UI boundaries.</summary>
public class CategoryDto
{
    public Guid? Id { get; set; }
    public long? Version { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? SortOrder { get; set; }
    public bool? IsActive { get; set; }
    public Guid? ParentCategoryId { get; set; }
}

/// <summary>Carries tag data across API, application, and UI boundaries.</summary>
public class TagDto
{
    public Guid? Id { get; set; }
    public long? Version { get; set; }
    public string? Name { get; set; }
    public string? Color { get; set; }
}

/// <summary>Carries comment data across API, application, and UI boundaries.</summary>
public class CommentDto
{
    public Guid? Id { get; set; }
    public long? Version { get; set; }
    public string? Body { get; set; }
    public Guid? TaskItemId { get; set; }
    public List<AttachmentDto>? Attachments { get; set; }
}

/// <summary>Carries checklist item data across API, application, and UI boundaries.</summary>
public class ChecklistItemDto
{
    public Guid? Id { get; set; }
    public long? Version { get; set; }
    public string? Title { get; set; }
    public bool? IsCompleted { get; set; }
    public int? SortOrder { get; set; }
    public DateTimeOffset? CompletedDate { get; set; }
    public Guid? TaskItemId { get; set; }
}

/// <summary>Carries attachment data across API, application, and UI boundaries.</summary>
public class AttachmentDto
{
    public Guid? Id { get; set; }
    public long? Version { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? StorageUri { get; set; }
    public string? OwnerType { get; set; }
    public Guid? OwnerId { get; set; }
}

/// <summary>TaskItem list request - the only list read that is cursor-only (offset paging removed, GR-18).</summary>
public class TaskItemCursorSearchRequest
{
    [JsonPropertyName("filter")]
    public TaskItemSearchFilter Filter { get; set; } = new();

    /// <summary>One of IdAsc, DueDateAsc, DueDateDesc, ModifiedDesc, StatusThenId.</summary>
    [JsonPropertyName("sortMode")]
    public string SortMode { get; set; } = "IdAsc";

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 50;

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

/// <summary>One keyset page. NextCursor is null exactly when HasMore is false.</summary>
public class CursorPage<T>
{
    [JsonPropertyName("items")]
    public List<T>? Items { get; set; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

/// <summary>One status bucket of the tenant task summary.</summary>
public class TaskItemStatusCountDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>Tenant-wide task counts computed in one database round trip.</summary>
public class TaskItemSummaryDto
{
    [JsonPropertyName("byStatus")]
    public List<TaskItemStatusCountDto>? ByStatus { get; set; }

    [JsonPropertyName("overdue")]
    public int Overdue { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; set; }
}

/// <summary>Full category and tag lists for pickers - replaces oversized PageSize search calls.</summary>
public class TaskMetadataDto
{
    [JsonPropertyName("categories")]
    public List<CategoryDto>? Categories { get; set; }

    [JsonPropertyName("tags")]
    public List<TagDto>? Tags { get; set; }

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; set; }
}

/// <summary>Carries search request CQRS data between endpoints and handlers.</summary>
public class SearchRequest<TFilter> where TFilter : class, new()
{
    [JsonPropertyName("filter")]
    public TFilter Filter { get; set; } = new();

    [JsonPropertyName("pageIndex")]
    public int PageIndex { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 50;
}

/// <summary>Carries paged transport data between the API contract and Uno client services.</summary>
public class PagedResponse<T>
{
    [JsonPropertyName("data")]
    public List<T>? Data { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("pageIndex")]
    public int PageIndex { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
}

/// <summary>Carries task item search transport data between the API contract and Uno client services.</summary>
public class TaskItemSearchFilter
{
    public string? SearchTerm { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public Guid? CategoryId { get; set; }
}

/// <summary>Carries category search transport data between the API contract and Uno client services.</summary>
public class CategorySearchFilter
{
    public string? SearchTerm { get; set; }
    public bool? IsActive { get; set; }
    public Guid? ParentCategoryId { get; set; }
}

/// <summary>Carries tag search transport data between the API contract and Uno client services.</summary>
public class TagSearchFilter
{
    public string? SearchTerm { get; set; }
}

/// <summary>Carries attachment search transport data between the API contract and Uno client services.</summary>
public class AttachmentSearchFilter
{
    public Guid? OwnerId { get; set; }
    public string? OwnerType { get; set; }
}

#endregion

#region Request Builders

/// <summary>Builds typed HTTP requests for the task items endpoint group in the hand-authored Uno API client.</summary>
public class TaskItemsRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes task items request builder with required dependencies and default state.</summary>
    public TaskItemsRequestBuilder(HttpClient http) => _http = http;

    public TaskItemByIdRequestBuilder this[Guid id] => new(_http, id);

    /// <summary>Searches with keyset (cursor) paging, filters, and a sort mode.</summary>
    public async Task<CursorPage<TaskItemDto>?> SearchAsync(TaskItemCursorSearchRequest request, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/task-items/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await TaskFlowApiJson.ReadAsync<CursorPage<TaskItemDto>>(response.Content, cancellationToken);
    }

    /// <summary>Tenant task counts by status, overdue, and total in one round trip.</summary>
    public async Task<TaskItemSummaryDto?> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        await TaskFlowApiJson.GetAsync<TaskItemSummaryDto>(_http, "/api/v1/task-items/summary", cancellationToken);

    /// <summary>Creates a new TaskItem. An optional caller-supplied UUIDv7 id makes the create idempotent.</summary>
    public async Task<TaskItemDto?> PostAsync(TaskItemDto dto, CancellationToken cancellationToken = default)
    {
        dto.Id ??= Guid.CreateVersion7();
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<TaskItemDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }
}

/// <summary>Builds typed HTTP requests for a single task item and its aggregate children.</summary>
public class TaskItemByIdRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    /// <summary>Initializes task item by ID request builder with required dependencies and default state.</summary>
    public TaskItemByIdRequestBuilder(HttpClient http, Guid id) { _http = http; _id = id; }

    public TaskItemCommentsRequestBuilder Comments => new(_http, _id);
    public TaskItemChecklistItemsRequestBuilder ChecklistItems => new(_http, _id);
    public TaskItemTagsRequestBuilder Tags => new(_http, _id);

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<TaskItemDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        var wrapper = await TaskFlowApiJson.GetAsync<DefaultResponse<TaskItemDto>>(_http, $"/api/v1/task-items/{_id}", cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Sends a PUT request. ifMatch is the expected Version (or "*" to overwrite unconditionally).</summary>
    public async Task<TaskItemDto?> PutAsync(TaskItemDto dto, string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/task-items/{_id}", new DefaultRequest<TaskItemDto> { Item = dto }, ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<TaskItemDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data. ifMatch is the expected Version (or "*" to overwrite unconditionally).</summary>
    public async Task DeleteAsync(string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.DeleteAsync(_http, $"/api/v1/task-items/{_id}", ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Comments are mutated only through the TaskItem aggregate root (GR-15) - there is no standalone
/// /comments write route. Child writes use the root's ETag as their If-Match currency (D-031).
/// </summary>
public class TaskItemCommentsRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _taskId;
    /// <summary>Initializes comments request builder with required dependencies and default state.</summary>
    public TaskItemCommentsRequestBuilder(HttpClient http, Guid taskId) { _http = http; _taskId = taskId; }

    /// <summary>Adds a Comment to the TaskItem.</summary>
    public async Task<CommentDto?> PostAsync(CommentDto dto, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, $"/api/v1/task-items/{_taskId}/comments", new DefaultRequest<CommentDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<CommentDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Removes a Comment from the TaskItem. ifMatch is the root's expected Version.</summary>
    public async Task DeleteAsync(Guid commentId, string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.DeleteAsync(_http, $"/api/v1/task-items/{_taskId}/comments/{commentId}", ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Checklist items are mutated only through the TaskItem aggregate root (GR-15).</summary>
public class TaskItemChecklistItemsRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _taskId;
    /// <summary>Initializes checklist items request builder with required dependencies and default state.</summary>
    public TaskItemChecklistItemsRequestBuilder(HttpClient http, Guid taskId) { _http = http; _taskId = taskId; }

    /// <summary>Adds a ChecklistItem to the TaskItem.</summary>
    public async Task<ChecklistItemDto?> PostAsync(ChecklistItemDto dto, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, $"/api/v1/task-items/{_taskId}/checklist-items", new DefaultRequest<ChecklistItemDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<ChecklistItemDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Updates a ChecklistItem on the TaskItem. ifMatch is the root's expected Version.</summary>
    public async Task<ChecklistItemDto?> PutAsync(Guid checklistItemId, ChecklistItemDto dto, string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/task-items/{_taskId}/checklist-items/{checklistItemId}", new DefaultRequest<ChecklistItemDto> { Item = dto }, ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<ChecklistItemDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Removes a ChecklistItem from the TaskItem. ifMatch is the root's expected Version.</summary>
    public async Task DeleteAsync(Guid checklistItemId, string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.DeleteAsync(_http, $"/api/v1/task-items/{_taskId}/checklist-items/{checklistItemId}", ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Tag associations are mutated only through the TaskItem aggregate root (GR-15).</summary>
public class TaskItemTagsRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _taskId;
    /// <summary>Initializes tags request builder with required dependencies and default state.</summary>
    public TaskItemTagsRequestBuilder(HttpClient http, Guid taskId) { _http = http; _taskId = taskId; }

    /// <summary>Associates an existing Tag with the TaskItem. No request body - both ids are route parameters.</summary>
    public async Task PostAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"/api/v1/task-items/{_taskId}/tags/{tagId}", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Removes a Tag association from the TaskItem. ifMatch is the root's expected Version.</summary>
    public async Task DeleteAsync(Guid tagId, string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.DeleteAsync(_http, $"/api/v1/task-items/{_taskId}/tags/{tagId}", ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Builds typed HTTP requests for the categories endpoint group in the hand-authored Uno API client.</summary>
public class CategoriesRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes categories request builder with required dependencies and default state.</summary>
    public CategoriesRequestBuilder(HttpClient http) => _http = http;

    public CategoriesSearchRequestBuilder Search => new(_http);
    public CategoryByIdRequestBuilder this[Guid id] => new(_http, id);

    /// <summary>Creates a new Category. An optional caller-supplied UUIDv7 id makes the create idempotent.</summary>
    public async Task<CategoryDto?> PostAsync(CategoryDto dto, CancellationToken cancellationToken = default)
    {
        dto.Id ??= Guid.CreateVersion7();
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/categories", new DefaultRequest<CategoryDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<CategoryDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }
}

/// <summary>Builds typed HTTP requests for the categories search endpoint group in the hand-authored Uno API client.</summary>
public class CategoriesSearchRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes categories search request builder with required dependencies and default state.</summary>
    public CategoriesSearchRequestBuilder(HttpClient http) => _http = http;

    /// <summary>Sends a POST request through categories search request builder and returns the typed response.</summary>
    public async Task<PagedResponse<CategoryDto>?> PostAsync(SearchRequest<CategorySearchFilter> request,
        CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/categories/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await TaskFlowApiJson.ReadAsync<PagedResponse<CategoryDto>>(response.Content, cancellationToken);
    }
}

/// <summary>Builds typed HTTP requests for the category by ID endpoint group in the hand-authored Uno API client.</summary>
public class CategoryByIdRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    /// <summary>Initializes category by ID request builder with required dependencies and default state.</summary>
    public CategoryByIdRequestBuilder(HttpClient http, Guid id) { _http = http; _id = id; }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<CategoryDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        var wrapper = await TaskFlowApiJson.GetAsync<DefaultResponse<CategoryDto>>(_http, $"/api/v1/categories/{_id}", cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Sends a PUT request. ifMatch is the expected Version (or "*" to overwrite unconditionally).</summary>
    public async Task<CategoryDto?> PutAsync(CategoryDto dto, string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/categories/{_id}", new DefaultRequest<CategoryDto> { Item = dto }, ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<CategoryDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data. ifMatch is the expected Version (or "*" to overwrite unconditionally).</summary>
    public async Task DeleteAsync(string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.DeleteAsync(_http, $"/api/v1/categories/{_id}", ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Builds typed HTTP requests for the tags endpoint group in the hand-authored Uno API client.</summary>
public class TagsRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes tags request builder with required dependencies and default state.</summary>
    public TagsRequestBuilder(HttpClient http) => _http = http;

    public TagsSearchRequestBuilder Search => new(_http);
    public TagByIdRequestBuilder this[Guid id] => new(_http, id);

    /// <summary>Creates a new Tag. An optional caller-supplied UUIDv7 id makes the create idempotent.</summary>
    public async Task<TagDto?> PostAsync(TagDto dto, CancellationToken cancellationToken = default)
    {
        dto.Id ??= Guid.CreateVersion7();
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/tags", new DefaultRequest<TagDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<TagDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }
}

/// <summary>Builds typed HTTP requests for the tags search endpoint group in the hand-authored Uno API client.</summary>
public class TagsSearchRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes tags search request builder with required dependencies and default state.</summary>
    public TagsSearchRequestBuilder(HttpClient http) => _http = http;

    /// <summary>Sends a POST request through tags search request builder and returns the typed response.</summary>
    public async Task<PagedResponse<TagDto>?> PostAsync(SearchRequest<TagSearchFilter> request,
        CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/tags/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await TaskFlowApiJson.ReadAsync<PagedResponse<TagDto>>(response.Content, cancellationToken);
    }
}

/// <summary>Builds typed HTTP requests for the tag by ID endpoint group in the hand-authored Uno API client.</summary>
public class TagByIdRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    /// <summary>Initializes tag by ID request builder with required dependencies and default state.</summary>
    public TagByIdRequestBuilder(HttpClient http, Guid id) { _http = http; _id = id; }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<TagDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        var wrapper = await TaskFlowApiJson.GetAsync<DefaultResponse<TagDto>>(_http, $"/api/v1/tags/{_id}", cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Sends a PUT request. ifMatch is the expected Version (or "*" to overwrite unconditionally).</summary>
    public async Task<TagDto?> PutAsync(TagDto dto, string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/tags/{_id}", new DefaultRequest<TagDto> { Item = dto }, ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<TagDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data. ifMatch is the expected Version (or "*" to overwrite unconditionally).</summary>
    public async Task DeleteAsync(string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.DeleteAsync(_http, $"/api/v1/tags/{_id}", ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Builds typed HTTP requests for the attachments endpoint group in the hand-authored Uno API client.</summary>
public class AttachmentsRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes attachments request builder with required dependencies and default state.</summary>
    public AttachmentsRequestBuilder(HttpClient http) => _http = http;

    public AttachmentsSearchRequestBuilder Search => new(_http);
    public AttachmentByIdRequestBuilder this[Guid id] => new(_http, id);

    /// <summary>Sends a POST request through attachments request builder and returns the typed response.</summary>
    public async Task<AttachmentDto?> PostAsync(AttachmentDto dto, CancellationToken cancellationToken = default)
    {
        dto.Id ??= Guid.CreateVersion7();
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/attachments", new DefaultRequest<AttachmentDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<AttachmentDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }
}

/// <summary>Builds typed HTTP requests for the attachments search endpoint group in the hand-authored Uno API client.</summary>
public class AttachmentsSearchRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes attachments search request builder with required dependencies and default state.</summary>
    public AttachmentsSearchRequestBuilder(HttpClient http) => _http = http;

    /// <summary>Sends a POST request through attachments search request builder and returns the typed response.</summary>
    public async Task<PagedResponse<AttachmentDto>?> PostAsync(SearchRequest<AttachmentSearchFilter> request,
        CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/attachments/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await TaskFlowApiJson.ReadAsync<PagedResponse<AttachmentDto>>(response.Content, cancellationToken);
    }
}

/// <summary>Builds typed HTTP requests for the attachment by ID endpoint group in the hand-authored Uno API client.</summary>
public class AttachmentByIdRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    /// <summary>Initializes attachment by ID request builder with required dependencies and default state.</summary>
    public AttachmentByIdRequestBuilder(HttpClient http, Guid id) { _http = http; _id = id; }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<AttachmentDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        var wrapper = await TaskFlowApiJson.GetAsync<DefaultResponse<AttachmentDto>>(_http, $"/api/v1/attachments/{_id}", cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data. ifMatch is the expected Version (or "*" to overwrite unconditionally).</summary>
    public async Task DeleteAsync(string ifMatch, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.DeleteAsync(_http, $"/api/v1/attachments/{_id}", ifMatch, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Full category and tag lists for pickers.</summary>
public class TaskMetadataRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes task metadata request builder with required dependencies and default state.</summary>
    public TaskMetadataRequestBuilder(HttpClient http) => _http = http;

    /// <summary>Loads the full category and tag lists.</summary>
    public async Task<TaskMetadataDto?> GetAsync(CancellationToken cancellationToken = default) =>
        await TaskFlowApiJson.GetAsync<TaskMetadataDto>(_http, "/api/v1/task-metadata", cancellationToken);
}

#endregion
