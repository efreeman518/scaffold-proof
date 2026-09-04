using TaskFlow.Application.Models;

namespace TaskFlow.Application.Models.Paging;

/// <summary>TaskItem list request - the only list read that is cursor-only (offset paging removed).</summary>
public record TaskItemCursorSearchRequest : CursorSearchRequest<TaskItemSearchFilter, TaskItemSortMode>;
