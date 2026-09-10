using EF.Common.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Infrastructure.AI.Search;

/// <summary>
/// Search fallback for deployments without Azure AI Search. It delegates to the SQL prefix search
/// (<c>Title.StartsWith</c> over IX_TaskItem_TenantId_Title_Id) instead of returning nothing: a local run,
/// a test, and the AI task tool all get real results, so search behavior is exercised without the cloud
/// dependency. What is lost is semantic, vector, and substring matching - every <see cref="SearchMode"/>
/// resolves to the same prefix query here, which is why the mode is logged rather than honored.
/// Indexing calls are no-ops because there is no index to maintain.
/// </summary>
public class NoOpSearchService(
    ITaskItemRepositoryQuery taskItemRepoQuery,
    ILogger<NoOpSearchService> logger) : ITaskFlowSearchService
{
    /// <summary>Searches search task items and returns filtered results for callers.</summary>
    public async Task<IReadOnlyList<TaskItemSearchResult>> SearchTaskItemsAsync(
        string query, SearchMode mode, Guid? tenantId, int maxResults = 10, CancellationToken ct = default)
    {
        logger.SearchNotConfiguredPrefixFallback(query, mode.ToString());

        var request = new TaskItemCursorSearchRequest
        {
            PageSize = Math.Clamp(maxResults, PageSizeLimits.Min, PageSizeLimits.Max),
            SortMode = TaskItemSortMode.IdAsc,
            Filter = new TaskItemSearchFilter
            {
                SearchTerm = query,
                TenantId = tenantId
            }
        };

        var page = await taskItemRepoQuery.SearchTaskItemsAsync(request, tenantId ?? Guid.Empty, ct);

        return [.. page.Items.Select(t => new TaskItemSearchResult
        {
            Id = t.Id?.ToString() ?? string.Empty,
            Title = t.Title,
            Description = t.Description,
            Status = t.Status.ToString(),
            Priority = t.Priority.ToString(),
            DueDate = t.DueDate
        })];
    }

    /// <summary>Provides the index task item operation for no op search service.</summary>
    public Task IndexTaskItemAsync(TaskItemSearchDocument document, CancellationToken ct = default)
    {
        logger.SearchNotConfiguredIndexSkipped(document.Id);
        return Task.CompletedTask;
    }

    /// <summary>Removes remove task item while keeping aggregate relationship state consistent.</summary>
    public Task RemoveTaskItemAsync(string taskItemId, CancellationToken ct = default)
    {
        logger.SearchNotConfiguredRemovalSkipped(taskItemId);
        return Task.CompletedTask;
    }
}
