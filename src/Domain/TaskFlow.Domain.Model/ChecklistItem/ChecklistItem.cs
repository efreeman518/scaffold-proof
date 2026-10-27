using EF.Domain;
using EF.Domain.Contracts;
using DomainChecklistItemId = TaskFlow.Domain.Shared.ChecklistItemId;
using DomainTaskItemId = TaskFlow.Domain.Shared.TaskItemId;
using DomainTenantId = TaskFlow.Domain.Shared.TenantId;

namespace TaskFlow.Domain.Model;

/// <summary>Models checklist item domain behavior and invariants.</summary>
public class ChecklistItem : TaskFlowEntityBase<DomainChecklistItemId>, ITenantEntity<DomainTenantId>
{
    public DomainTenantId TenantId { get; init; }
    public string Title { get; private set; } = null!;
    public bool IsCompleted { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset? CompletedDate { get; private set; }

    // Foreign key
    public DomainTaskItemId TaskItemId { get; private set; }

    // Navigation
    public TaskItem TaskItem { get; private set; } = null!;

    /// <summary>Initializes checklist item with required dependencies and default state.</summary>
    private ChecklistItem() { }

    /// <summary>Initializes checklist item with required dependencies and default state.</summary>
    private ChecklistItem(DomainTenantId tenantId, DomainTaskItemId taskItemId, string title, int sortOrder, DomainChecklistItemId? id)
    {
        if (id.HasValue) Id = id.Value; // D-033: caller-supplied UUIDv7 id makes create idempotent.
        TenantId = tenantId;
        TaskItemId = taskItemId;
        Title = title;
        SortOrder = sortOrder;
        IsCompleted = false;
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public static Result<ChecklistItem> Create(DomainTenantId tenantId, DomainTaskItemId taskItemId, string title, int sortOrder = 0, DomainChecklistItemId? id = null)
    {
        var entity = new ChecklistItem(tenantId, taskItemId, title, sortOrder, id);
        return entity.Valid();
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public Result<ChecklistItem> Update(string? title = null, bool? isCompleted = null, int? sortOrder = null)
    {
        if (title is not null) Title = title;
        if (sortOrder.HasValue) SortOrder = sortOrder.Value;
        if (isCompleted.HasValue)
        {
            IsCompleted = isCompleted.Value;
            CompletedDate = isCompleted.Value ? DateTimeOffset.UtcNow : null;
        }
        return Valid();
    }

    /// <summary>Creates a valid checklist item instance with domain-required defaults.</summary>
    private Result<ChecklistItem> Valid()
    {
        var errors = new List<ValidationIssue>();
        if (TenantId.Value == Guid.Empty) errors.Add(new ValidationIssue("Tenant ID cannot be empty.", nameof(TenantId)));
        if (TaskItemId.Value == Guid.Empty) errors.Add(new ValidationIssue("Task Item ID cannot be empty.", nameof(TaskItemId)));
        if (string.IsNullOrWhiteSpace(Title)) errors.Add(new ValidationIssue("Title is required.", nameof(Title)));
        return errors.Count > 0
            ? new ValidationError(errors)
            : this;
    }
}
