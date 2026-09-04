namespace TaskFlow.Application.Contracts.Storage;

/// <summary>Persists and queries i task view data through infrastructure storage contracts.</summary>
public interface ITaskViewRepository
{
    /// <summary>Writes upsert to the configured read model store.</summary>
    Task UpsertAsync(TaskViewDto taskView, CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<TaskViewDto?> GetAsync(string id, string tenantId, CancellationToken ct = default);
    /// <summary>
    /// Reads one page for a tenant and returns the store continuation token so the caller can resume.
    /// Dropping the token, as the previous signature did, made every page the first page.
    /// </summary>
    Task<TaskViewPage> QueryByTenantAsync(string tenantId,
        int pageSize = 20, string? continuationToken = null, CancellationToken ct = default);

    /// <summary>Applies counter deltas to one document in place; missing documents are ignored.</summary>
    Task PatchCountersAsync(string id, string tenantId,
        IReadOnlyDictionary<string, int> increments, DateTimeOffset lastModifiedUtc, CancellationToken ct = default);
    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    Task DeleteAsync(string id, string tenantId, CancellationToken ct = default);
}

/// <summary>One page of task views plus the token that resumes the query.</summary>
/// <param name="Items">Documents in this page.</param>
/// <param name="ContinuationToken">Token for the next page, or null at the end of the result set.</param>
public sealed record TaskViewPage(IReadOnlyList<TaskViewDto> Items, string? ContinuationToken);

/// <summary>Carries task view data across API, application, and UI boundaries.</summary>
public class TaskViewDto
{
    public string Id { get; set; } = null!;
    public string TenantId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string Status { get; set; } = null!;
    public string Priority { get; set; } = null!;
    public string? CategoryName { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public DateTimeOffset? CompletedDate { get; set; }
    public bool IsOverdue { get; set; }
    public List<string> Tags { get; set; } = [];
    public int CommentCount { get; set; }
    public int ChecklistTotal { get; set; }
    public int ChecklistCompleted { get; set; }
    public int AttachmentCount { get; set; }
    public int SubTaskCount { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}
