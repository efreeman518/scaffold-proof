namespace TaskFlow.Application.Models.Paging;

/// <summary>
/// Keyset sort orders supported by the TaskItem cursor search. Each mode maps to one covering index
/// on (TenantId, sortKey, Id) - see TaskItemRepositoryQuery for the index named per arm.
/// </summary>
public enum TaskItemSortMode
{
    IdAsc = 0,
    DueDateAsc = 1,
    DueDateDesc = 2,
    ModifiedDesc = 3,
    StatusThenId = 4
}
