using EF.Common.Contracts;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Scheduler.Abstractions;

namespace TaskFlow.Scheduler.Handlers;

/// <summary>Handles recurring task generation work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
public class RecurringTaskGenerationHandler : IScheduledJobHandler
{
    private readonly ITaskItemService _taskItemService;
    private readonly ILogger<RecurringTaskGenerationHandler> _logger;

    /// <summary>Initializes recurring task generation handler with required dependencies and default state.</summary>
    public RecurringTaskGenerationHandler(ITaskItemService taskItemService, ILogger<RecurringTaskGenerationHandler> logger)
    {
        _taskItemService = taskItemService;
        _logger = logger;
    }

    /// <summary>Handles recurring task generation requests and returns the application result.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        _logger.LogInformation("Generating recurring tasks...");

        // Compile-level pass-through to the cursor contract: the placeholder job reads one page and
        // PageSize 500 is now outside the enforced [1,100] range. Real paging belongs with the job
        // implementations themselves.
        var request = new TaskItemCursorSearchRequest
        {
            PageSize = PageSizeLimits.Max,
            Filter = new TaskItemSearchFilter()
        };

        var result = await _taskItemService.SearchAsync(request, ct);

        var recurringTasks = result.Data?
            .Where(t => !string.IsNullOrEmpty(t.RecurrenceFrequency))
            .ToList() ?? [];

        _logger.RecurringTemplatesFound(recurringTasks.Count);

        // Future: evaluate recurrence patterns and create new task instances
    }
}
