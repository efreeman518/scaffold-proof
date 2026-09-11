using EF.Common.Contracts;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Models.Paging;

/// <summary>
/// TaskItem list request - the only list read that is cursor-only (offset paging removed). The generic
/// shape (Filter, SortMode, PageSize, Cursor) is <see cref="CursorSearchRequest{TFilter, TSortMode}"/>
/// from EF.Common.Contracts (package request 21); the default page size is TaskFlow's, because the
/// package record leaves <c>PageSize</c> at 0 and this type is what an omitted request body binds to.
/// </summary>
public record TaskItemCursorSearchRequest : CursorSearchRequest<TaskItemSearchFilter, TaskItemSortMode>
{
    public TaskItemCursorSearchRequest() => PageSize = PageSizeLimits.Default;
}
