using EF.Common.Contracts;
using Microsoft.Extensions.Logging;
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
    ITaskFlowSearchService searchService)
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

        // The loaded version is the If-Match currency; the tool retries nothing itself, so a
        // ConcurrencyMismatchException surfacing here is the correct signal that the task moved.
        var updateResult = await taskItemService.UpdateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, dto.Version);
        if (updateResult.IsFailure) return $"Failed to update status: {updateResult.ErrorMessage}";

        return $"Updated task '{dto.Title}' status to {newStatus}.";
    }

    /// <summary>
    /// Produces a lightweight backlog summary from the normal task search endpoint rather than a
    /// separate analytics store.
    /// </summary>
    public async Task<string> SummarizeBacklog()
    {
        logger.LogDebug("Agent tool: SummarizeBacklog");

        var request = new TaskItemCursorSearchRequest
        {
            PageSize = 100
        };

        var page = await taskItemService.SearchAsync(request);
        var tasks = page.Data;

        var byStatus = tasks.GroupBy(t => t.Status)
            .Select(g => $"  {g.Key}: {g.Count()}")
            .ToList();

        var overdue = tasks.Count(t => t.DueDate.HasValue && t.DueDate < DateTimeOffset.UtcNow
                                      && t.Status != TaskFlow.Domain.Shared.Enums.TaskItemStatus.Completed
                                      && t.Status != TaskFlow.Domain.Shared.Enums.TaskItemStatus.Cancelled);

        return $"""
            Task Summary ({tasks.Count} total):
            Status breakdown:
            {string.Join("\n", byStatus)}
            Overdue: {overdue}
            """;
    }
}
