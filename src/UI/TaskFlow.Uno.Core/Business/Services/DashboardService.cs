using TaskFlow.Uno.Core.Business.Models;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>Coordinates dashboard application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public class DashboardService(ITaskItemApiService taskItemService) : IDashboardService
{
    /// <summary>
    /// One tenant-wide summary call for the tiles, plus one cursor page for the recent-activity list -
    /// replaces the old "fetch every task, count client-side" pattern (SearchAsync no longer exists).
    /// </summary>
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var summary = await taskItemService.GetSummaryAsync(ct);
        var recent = await taskItemService.SearchCursorAsync(sortMode: "ModifiedDesc", pageSize: 10, ct: ct);

        return new DashboardSummary
        {
            TotalTasks = summary.Total,
            OpenTasks = CountOf(summary, "Open"),
            InProgressTasks = CountOf(summary, "InProgress"),
            CompletedTasks = CountOf(summary, "Completed"),
            BlockedTasks = CountOf(summary, "Blocked"),
            CancelledTasks = CountOf(summary, "Cancelled"),
            OverdueTasks = summary.Overdue,
            RecentActivity = recent.Items
        };
    }

    private static int CountOf(TaskItemSummaryModel summary, string status) =>
        summary.ByStatus.TryGetValue(status, out var count) ? count : 0;
}
