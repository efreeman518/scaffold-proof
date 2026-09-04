using EF.Domain;
using EF.Domain.Contracts;
using DomainCommentId = TaskFlow.Domain.Shared.CommentId;
using DomainTaskItemId = TaskFlow.Domain.Shared.TaskItemId;
using DomainTenantId = TaskFlow.Domain.Shared.TenantId;

namespace TaskFlow.Domain.Model;

/// <summary>Models comment domain behavior and invariants.</summary>
public class Comment : TaskFlowEntityBase<DomainCommentId>, ITenantEntity<DomainTenantId>
{
    public DomainTenantId TenantId { get; init; }
    public string Body { get; private set; } = null!;

    // Foreign key
    public DomainTaskItemId TaskItemId { get; private set; }

    // Navigation
    public TaskItem TaskItem { get; private set; } = null!;

    /// <summary>Initializes comment with required dependencies and default state.</summary>
    private Comment() { }

    /// <summary>Initializes comment with required dependencies and default state.</summary>
    private Comment(DomainTenantId tenantId, DomainTaskItemId taskItemId, string body)
    {
        TenantId = tenantId;
        TaskItemId = taskItemId;
        Body = body;
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public static DomainResult<Comment> Create(DomainTenantId tenantId, DomainTaskItemId taskItemId, string body)
    {
        var entity = new Comment(tenantId, taskItemId, body);
        return entity.Valid();
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public DomainResult<Comment> Update(string? body = null)
    {
        if (body is not null) Body = body;
        return Valid();
    }

    /// <summary>Creates a valid comment instance with domain-required defaults.</summary>
    private DomainResult<Comment> Valid()
    {
        var errors = new List<DomainError>();
        if (TenantId.Value == Guid.Empty) errors.Add(DomainError.Create("Tenant ID cannot be empty."));
        if (TaskItemId.Value == Guid.Empty) errors.Add(DomainError.Create("Task Item ID cannot be empty."));
        if (string.IsNullOrWhiteSpace(Body)) errors.Add(DomainError.Create("Body is required."));
        return errors.Count > 0
            ? DomainResult<Comment>.Failure(errors)
            : DomainResult<Comment>.Success(this);
    }
}
