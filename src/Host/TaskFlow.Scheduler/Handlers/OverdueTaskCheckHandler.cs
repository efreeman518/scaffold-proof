using EF.Common.Contracts;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Scheduler.Abstractions;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>Handles overdue task check work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
public class OverdueTaskCheckHandler : IScheduledJobHandler
{
    private readonly ITaskItemService _taskItemService;
    private readonly ILogger<OverdueTaskCheckHandler> _logger;

    /// <summary>Initializes overdue task check handler with required dependencies and default state.</summary>
    public OverdueTaskCheckHandler(ITaskItemService taskItemService, ILogger<OverdueTaskCheckHandler> logger)
    {
        _taskItemService = taskItemService;
        _logger = logger;
    }

    /// <summary>Handles overdue task check requests and returns the application result.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        _logger.LogInformation("Checking for overdue tasks...");

        // Compile-level pass-through to the cursor contract: the placeholder job reads one page and
        // PageSize 500 is now outside the enforced [1,100] range. Real paging belongs with the job
        // implementations themselves.
        var request = new TaskItemCursorSearchRequest
        {
            PageSize = PageSizeLimits.Max,
            Filter = new TaskItemSearchFilter
            {
                IsOverdue = true
            }
        };

        var result = await _taskItemService.SearchAsync(request, ct);

        var overdueTasks = result.Data?
            .Where(t => t.Status != TaskItemStatus.Completed
                && t.Status != TaskItemStatus.Cancelled)
            .ToList() ?? [];

        _logger.OverdueTasksFound(overdueTasks.Count);

        // Future: publish TaskItemOverdueSuspected domain events for notification/escalation
    }
}
