using EF.Common.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Infrastructure.AI.Search;

namespace TaskFlow.Infrastructure.AI.Agents.Tools;

/// <summary>
/// Function-tool boundary exposed to the task assistant agent. Tools reuse application services
/// instead of repositories so validation, tenant filtering, status rules, audit, and integration
/// event publishing stay the same as normal API calls.
/// </summary>
public class TaskItemTools(
    ILogger<TaskItemTools> logger,
    ITaskItemService taskItemService,
    ITaskFlowSearchService searchService,
    ITaskFlowReadService readService)
{
    /// <summary>
    /// Searches via Azure AI Search when configured, or the no-op search service in scaffold mode.
    /// Status and priority parameters are accepted for agent affordance but not yet pushed into the
    /// search filter.
    /// </summary>
    public async Task<string> SearchTasks(string query, string status = "", string priority = "")
    {
        logger.AgentSearchTasks(query, status, priority);

        var results = await searchService.SearchTaskItemsAsync(query, SearchMode.Hybrid, tenantId: null, maxResults: 10);

        if (results.Count == 0)
            return "No tasks found matching your query.";

        var lines = results.Select(r =>
            $"- [{r.Id}] {r.Title} (Status: {r.Status}, Priority: {r.Priority}, Due: {r.DueDate?.ToString("yyyy-MM-dd") ?? "none"})");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Reads through the application service so tenant-boundary checks match the API path.
    /// </summary>
    public async Task<string> GetTaskDetails(string taskId)
    {
        logger.AgentGetTaskDetails(taskId);

        if (!Guid.TryParse(taskId, out var id))
            return $"Invalid task ID: {taskId}";

        var result = await taskItemService.GetAsync(id);
        if (result.IsNone) return $"Task {taskId} not found.";
        if (result.IsFailure) return $"Error retrieving task: {result.ErrorMessage}";

        var task = result.Value!.Item!;
        return $"""
            Task: {task.Title}
            ID: {task.Id}
            Status: {task.Status}
            Priority: {task.Priority}
            Description: {task.Description ?? "(none)"}
            Category: {task.CategoryName ?? "(none)"}
            Due: {task.DueDate?.ToString("yyyy-MM-dd") ?? "(none)"}
            Checklist: {task.ChecklistItems?.Count ?? 0} items
            Comments: {task.Comments?.Count ?? 0}
            """;
    }

    /// <summary>
    /// Creates a task through the application service. The request context supplies tenant and user
    /// metadata; the agent only provides business fields.
    /// </summary>
    public async Task<string> CreateTask(string title, string description = "", string priority = "")
    {
        logger.AgentCreateTask(title);

        var dto = new TaskItemDto
        {
            // Client-generated UUIDv7 makes the create idempotent (D-033): a retried tool call with an
            // identical payload replays the existing task instead of creating a duplicate.
            Id = Guid.CreateVersion7(),
            Title = title,
            Description = description,
            Priority = Enum.TryParse<TaskFlow.Domain.Shared.Enums.Priority>(priority, true, out var p)
                ? p : TaskFlow.Domain.Shared.Enums.Priority.None
        };

        var result = await taskItemService.CreateAsync(new DefaultRequest<TaskItemDto> { Item = dto });
        if (result.IsFailure) return $"Failed to create task: {result.ErrorMessage}";

        return $"Created task '{title}' with ID {result.Value!.Item!.Id}";
    }

    /// <summary>
    /// Loads the current task, changes only Status, and delegates transition validation to the
    /// aggregate through the application service.
    /// </summary>
    public async Task<string> UpdateTaskStatus(string taskId, string newStatus)
    {
        logger.AgentUpdateTaskStatus(taskId, newStatus);

        if (!Guid.TryParse(taskId, out var id))
            return $"Invalid task ID: {taskId}";

        if (!Enum.TryParse<TaskFlow.Domain.Shared.Enums.TaskItemStatus>(newStatus, true, out var status))
            return $"Invalid status: {newStatus}. Valid values: Open, InProgress, Completed, Cancelled, Blocked.";

        var getResult = await taskItemService.GetAsync(id);
        if (getResult.IsNone) return $"Task {taskId} not found.";
        if (getResult.IsFailure) return $"Error: {getResult.ErrorMessage}";

        var dto = getResult.Value!.Item!;
        dto.Status = status;

        // The loaded version is the If-Match currency. The tool retries nothing itself - a
        // ConcurrencyMismatchException means the task changed between the read above and this write,
        // which the caller (agent or user) resolves by asking again rather than the tool silently
        // overwriting someone else's change.
        try
        {
            var updateResult = await taskItemService.UpdateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, dto.Version);
            if (updateResult.IsFailure) return $"Failed to update status: {updateResult.ErrorMessage}";
        }
        catch (ConcurrencyMismatchException)
        {
            return "Task changed while updating; retry.";
        }

        return $"Updated task '{dto.Title}' status to {newStatus}.";
    }

    /// <summary>
    /// Produces a backlog summary from the tenant-wide summary endpoint (one database round trip)
    /// instead of paging every task client-side.
    /// </summary>
    public async Task<string> SummarizeBacklog()
    {
        logger.AgentSummarizeBacklog();

        var summary = await readService.GetTaskItemSummaryAsync();

        var byStatus = summary.ByStatus
            .Select(s => $"  {s.Status}: {s.Count}")
            .ToList();

        return $"""
            Task Summary ({summary.Total} total):
            Status breakdown:
            {string.Join("\n", byStatus)}
            Overdue: {summary.Overdue}
            """;
    }
}
