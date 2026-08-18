using System.Text.Json.Serialization;

// Kiota client stub - replace with Kiota-generated client when OpenAPI spec is available.
// This stub provides the typed navigation structure matching the API surface.

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
    public CommentsRequestBuilder Comments => new(_http);
    public ChecklistItemsRequestBuilder ChecklistItems => new(_http);
    public AttachmentsRequestBuilder Attachments => new(_http);
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
    public string? Name { get; set; }
    public string? Color { get; set; }
}

/// <summary>Carries comment data across API, application, and UI boundaries.</summary>
public class CommentDto
{
    public Guid? Id { get; set; }
    public string? Body { get; set; }
    public Guid? TaskItemId { get; set; }
    public List<AttachmentDto>? Attachments { get; set; }
}

/// <summary>Carries checklist item data across API, application, and UI boundaries.</summary>
public class ChecklistItemDto
{
    public Guid? Id { get; set; }
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
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? StorageUri { get; set; }
    public string? OwnerType { get; set; }
    public Guid? OwnerId { get; set; }
}

/// <summary>Carries search request CQRS data between endpoints and handlers.</summary>
public class SearchRequest<TFilter> where TFilter : class, new()
{
    [System.Text.Json.Serialization.JsonPropertyName("filter")]
    public TFilter Filter { get; set; } = new();

    // Wire property name is `pageIndex` but the value is 1-based - that's
    // the contract the server actually binds. Do NOT also serialize
    // pageNumber - the server's linked setter will clobber it.
    [System.Text.Json.Serialization.JsonIgnore]
    public int PageNumber { get; set; } = 1;

    [System.Text.Json.Serialization.JsonPropertyName("pageIndex")]
    public int PageIndex
    {
        get => PageNumber;
        set => PageNumber = value;
    }

    [System.Text.Json.Serialization.JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 50;
}

/// <summary>Carries paged transport data between the API contract and Uno client services.</summary>
public class PagedResponse<T>
{
    [JsonPropertyName("items")]
    public List<T>? Items { get; set; }

    // The API currently returns `data` rather than `items` for paged responses.
    // Mirror that payload into Items so the rest of the Uno services can stay typed.
    [JsonPropertyName("data")]
    public List<T>? Data
    {
        get => Items;
        set => Items = value;
    }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("total")]
    public int Total
    {
        get => TotalCount;
        set => TotalCount = value;
    }

    // The server's paging contract is 1-based on BOTH request and response:
    // sent as `pageIndex` and echoed back as `pageIndex`. PageNumber is the
    // client-side 1-based alias; no offset conversion.
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("pageIndex")]
    public int PageIndex
    {
        get => PageNumber;
        set => PageNumber = value;
    }

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

/// <summary>Carries comment search transport data between the API contract and Uno client services.</summary>
public class CommentSearchFilter
{
    public Guid? TaskItemId { get; set; }
}

/// <summary>Carries checklist item search transport data between the API contract and Uno client services.</summary>
public class ChecklistItemSearchFilter
{
    public Guid? TaskItemId { get; set; }
    public bool? IsCompleted { get; set; }
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

    public TaskItemsSearchRequestBuilder Search => new(_http);
    public TaskItemByIdRequestBuilder this[Guid id] => new(_http, id);

    /// <summary>Sends a POST request through task items request builder and returns the typed response.</summary>
    public async Task<TaskItemDto?> PostAsync(TaskItemDto dto, CancellationToken cancellationToken = default)
    {
        NormalizeChildTaskItemIds(dto, dto.Id ?? Guid.Empty);
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<TaskItemDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>
    /// Fills child TaskItemId values from the parent route/body id so create and update payloads
    /// can use one parent envelope even when the UI buffered new child rows locally.
    /// </summary>
    internal static void NormalizeChildTaskItemIds(TaskItemDto dto, Guid fallbackTaskItemId)
    {
        if (dto.Comments != null)
        {
            foreach (var comment in dto.Comments)
            {
                comment.TaskItemId ??= fallbackTaskItemId;
            }
        }

        if (dto.ChecklistItems != null)
        {
            foreach (var checklistItem in dto.ChecklistItems)
            {
                checklistItem.TaskItemId ??= fallbackTaskItemId;
            }
        }
    }
}

/// <summary>Builds typed HTTP requests for the task items search endpoint group in the hand-authored Uno API client.</summary>
public class TaskItemsSearchRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes task items search request builder with required dependencies and default state.</summary>
    public TaskItemsSearchRequestBuilder(HttpClient http) => _http = http;

    /// <summary>Sends a POST request through task items search request builder and returns the typed response.</summary>
    public async Task<PagedResponse<TaskItemDto>?> PostAsync(SearchRequest<TaskItemSearchFilter> request,
        CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/task-items/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await TaskFlowApiJson.ReadAsync<PagedResponse<TaskItemDto>>(response.Content, cancellationToken);
    }
}

/// <summary>Builds typed HTTP requests for the task item by ID endpoint group in the hand-authored Uno API client.</summary>
public class TaskItemByIdRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    /// <summary>Initializes task item by ID request builder with required dependencies and default state.</summary>
    public TaskItemByIdRequestBuilder(HttpClient http, Guid id) { _http = http; _id = id; }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<TaskItemDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        var wrapper = await TaskFlowApiJson.GetAsync<DefaultResponse<TaskItemDto>>(_http, $"/api/v1/task-items/{_id}", cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Sends a PUT request through task item by ID request builder and returns the typed response.</summary>
    public async Task<TaskItemDto?> PutAsync(TaskItemDto dto, CancellationToken cancellationToken = default)
    {
        TaskItemsRequestBuilder.NormalizeChildTaskItemIds(dto, _id);
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/task-items/{_id}", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<TaskItemDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/task-items/{_id}", cancellationToken);
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

    /// <summary>Sends a POST request through categories request builder and returns the typed response.</summary>
    public async Task<CategoryDto?> PostAsync(CategoryDto dto, CancellationToken cancellationToken = default)
    {
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

    /// <summary>Sends a PUT request through category by ID request builder and returns the typed response.</summary>
    public async Task<CategoryDto?> PutAsync(CategoryDto dto, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/categories/{_id}", new DefaultRequest<CategoryDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<CategoryDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/categories/{_id}", cancellationToken);
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

    /// <summary>Sends a POST request through tags request builder and returns the typed response.</summary>
    public async Task<TagDto?> PostAsync(TagDto dto, CancellationToken cancellationToken = default)
    {
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

    /// <summary>Sends a PUT request through tag by ID request builder and returns the typed response.</summary>
    public async Task<TagDto?> PutAsync(TagDto dto, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/tags/{_id}", new DefaultRequest<TagDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<TagDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/tags/{_id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Builds typed HTTP requests for the comments endpoint group in the hand-authored Uno API client.</summary>
public class CommentsRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes comments request builder with required dependencies and default state.</summary>
    public CommentsRequestBuilder(HttpClient http) => _http = http;

    public CommentsSearchRequestBuilder Search => new(_http);
    public CommentByIdRequestBuilder this[Guid id] => new(_http, id);

    /// <summary>Sends a POST request through comments request builder and returns the typed response.</summary>
    public async Task<CommentDto?> PostAsync(CommentDto dto, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/comments", new DefaultRequest<CommentDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<CommentDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }
}

/// <summary>Builds typed HTTP requests for the comments search endpoint group in the hand-authored Uno API client.</summary>
public class CommentsSearchRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes comments search request builder with required dependencies and default state.</summary>
    public CommentsSearchRequestBuilder(HttpClient http) => _http = http;

    /// <summary>Sends a POST request through comments search request builder and returns the typed response.</summary>
    public async Task<PagedResponse<CommentDto>?> PostAsync(SearchRequest<CommentSearchFilter> request,
        CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/comments/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await TaskFlowApiJson.ReadAsync<PagedResponse<CommentDto>>(response.Content, cancellationToken);
    }
}

/// <summary>Builds typed HTTP requests for the comment by ID endpoint group in the hand-authored Uno API client.</summary>
public class CommentByIdRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    /// <summary>Initializes comment by ID request builder with required dependencies and default state.</summary>
    public CommentByIdRequestBuilder(HttpClient http, Guid id) { _http = http; _id = id; }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<CommentDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        var wrapper = await TaskFlowApiJson.GetAsync<DefaultResponse<CommentDto>>(_http, $"/api/v1/comments/{_id}", cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Sends a PUT request through comment by ID request builder and returns the typed response.</summary>
    public async Task<CommentDto?> PutAsync(CommentDto dto, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/comments/{_id}", new DefaultRequest<CommentDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<CommentDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/comments/{_id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Builds typed HTTP requests for the checklist items endpoint group in the hand-authored Uno API client.</summary>
public class ChecklistItemsRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes checklist items request builder with required dependencies and default state.</summary>
    public ChecklistItemsRequestBuilder(HttpClient http) => _http = http;

    public ChecklistItemsSearchRequestBuilder Search => new(_http);
    public ChecklistItemByIdRequestBuilder this[Guid id] => new(_http, id);

    /// <summary>Sends a POST request through checklist items request builder and returns the typed response.</summary>
    public async Task<ChecklistItemDto?> PostAsync(ChecklistItemDto dto, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/checklist-items", new DefaultRequest<ChecklistItemDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<ChecklistItemDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }
}

/// <summary>Builds typed HTTP requests for the checklist items search endpoint group in the hand-authored Uno API client.</summary>
public class ChecklistItemsSearchRequestBuilder
{
    private readonly HttpClient _http;
    /// <summary>Initializes checklist items search request builder with required dependencies and default state.</summary>
    public ChecklistItemsSearchRequestBuilder(HttpClient http) => _http = http;

    /// <summary>Sends a POST request through checklist items search request builder and returns the typed response.</summary>
    public async Task<PagedResponse<ChecklistItemDto>?> PostAsync(SearchRequest<ChecklistItemSearchFilter> request,
        CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PostAsync(_http, "/api/v1/checklist-items/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await TaskFlowApiJson.ReadAsync<PagedResponse<ChecklistItemDto>>(response.Content, cancellationToken);
    }
}

/// <summary>Builds typed HTTP requests for the checklist item by ID endpoint group in the hand-authored Uno API client.</summary>
public class ChecklistItemByIdRequestBuilder
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    /// <summary>Initializes checklist item by ID request builder with required dependencies and default state.</summary>
    public ChecklistItemByIdRequestBuilder(HttpClient http, Guid id) { _http = http; _id = id; }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<ChecklistItemDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        var wrapper = await TaskFlowApiJson.GetAsync<DefaultResponse<ChecklistItemDto>>(_http, $"/api/v1/checklist-items/{_id}", cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Sends a PUT request through checklist item by ID request builder and returns the typed response.</summary>
    public async Task<ChecklistItemDto?> PutAsync(ChecklistItemDto dto, CancellationToken cancellationToken = default)
    {
        var response = await TaskFlowApiJson.PutAsync(_http, $"/api/v1/checklist-items/{_id}", new DefaultRequest<ChecklistItemDto> { Item = dto }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wrapper = await TaskFlowApiJson.ReadAsync<DefaultResponse<ChecklistItemDto>>(response.Content, cancellationToken);
        return wrapper?.Item;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/checklist-items/{_id}", cancellationToken);
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

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/attachments/{_id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

#endregion
