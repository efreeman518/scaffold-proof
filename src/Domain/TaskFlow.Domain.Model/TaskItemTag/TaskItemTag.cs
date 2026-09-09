using EF.Domain;
using EF.Domain.Contracts;
using DomainTagId = TaskFlow.Domain.Shared.TagId;
using DomainTaskItemId = TaskFlow.Domain.Shared.TaskItemId;
using DomainTaskItemTagId = TaskFlow.Domain.Shared.TaskItemTagId;
using DomainTenantId = TaskFlow.Domain.Shared.TenantId;

namespace TaskFlow.Domain.Model;

/// <summary>Models task item tag domain behavior and invariants.</summary>
public class TaskItemTag : TaskFlowEntityBase<DomainTaskItemTagId>, ITenantEntity<DomainTenantId>
{
    public DomainTenantId TenantId { get; init; }

    // Composite key
    public DomainTaskItemId TaskItemId { get; private set; }
    public DomainTagId TagId { get; private set; }

    // Navigation
    public TaskItem TaskItem { get; private set; } = null!;
    public Tag Tag { get; private set; } = null!;

    /// <summary>Initializes task item tag with required dependencies and default state.</summary>
    private TaskItemTag() { }

    /// <summary>Initializes task item tag with required dependencies and default state.</summary>
    private TaskItemTag(DomainTenantId tenantId, DomainTaskItemId taskItemId, DomainTagId tagId)
    {
        TenantId = tenantId;
        TaskItemId = taskItemId;
        TagId = tagId;
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public static DomainResult<TaskItemTag> Create(DomainTenantId tenantId, DomainTaskItemId taskItemId, DomainTagId tagId)
    {
        var entity = new TaskItemTag(tenantId, taskItemId, tagId);
        return entity.Valid();
    }

    /// <summary>Creates a valid task item tag instance with domain-required defaults.</summary>
    private DomainResult<TaskItemTag> Valid()
    {
        var errors = new List<DomainError>();
        if (TenantId.Value == Guid.Empty) errors.Add(DomainError.Create("Tenant ID cannot be empty."));
        if (TaskItemId.Value == Guid.Empty) errors.Add(DomainError.Create("Task Item ID cannot be empty."));
        if (TagId.Value == Guid.Empty) errors.Add(DomainError.Create("Tag ID cannot be empty."));
        return errors.Count > 0
            ? DomainResult<TaskItemTag>.Failure(errors)
            : DomainResult<TaskItemTag>.Success(this);
    }
}
