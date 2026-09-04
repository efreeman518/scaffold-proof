using EF.Domain;
using EF.Domain.Contracts;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Constants;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Domain.Shared.Events;
using DomainCategoryId = TaskFlow.Domain.Shared.CategoryId;
using DomainTaskItemId = TaskFlow.Domain.Shared.TaskItemId;
using DomainTenantId = TaskFlow.Domain.Shared.TenantId;

namespace TaskFlow.Domain.Model;

/// <summary>
/// Task aggregate root. Owns task lifecycle rules, value-object updates, and local child
/// collection mutations before repositories persist the graph.
/// </summary>
public class TaskItem : TaskFlowEntityBase<DomainTaskItemId>, ITenantEntity<DomainTenantId>, IHasDomainEvents
{
    // D-026: events raised here are staged as outbox rows by OutboxStagingInterceptor in the same SaveChanges.
    private readonly DomainEventContainer _domainEvents = new();

    /// <inheritdoc />
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.Events;

    /// <inheritdoc />
    public void ClearDomainEvents() => _domainEvents.Clear();

    public DomainTenantId TenantId { get; init; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public Priority Priority { get; private set; }
    public TaskItemStatus Status { get; private set; }
    public TaskFeatures Features { get; private set; }
    public decimal? EstimatedEffort { get; private set; }
    public decimal? ActualEffort { get; private set; }
    public DateTimeOffset? CompletedDate { get; private set; }

    // Sensitive properties - persisted through the application-layer column encryptor (D-023):
    // both are stored randomized (AES-GCM); SecureDeterministic is additionally equality-queryable
    // through an HMAC blind-index sibling column populated by the persistence layer.
    public string? SecureDeterministic { get; private set; }
    public string? SecureRandom { get; private set; }

    // First-class scheduling dates (UTC). DateRange below is composed from them and is not mapped:
    // an index spanning owner and owned-type properties is not expressible in EF.
    public DateTimeOffset? StartDate { get; private set; }
    public DateTimeOffset? DueDate { get; private set; }

    // Scale columns (D-020 plan, Phase 3 jobs own the behavior). TerminalAtUtc is maintained here because
    // it is a pure consequence of the status state machine.
    public DateTimeOffset? TerminalAtUtc { get; private set; }
    public DateTimeOffset? NextOccurrenceAtUtc { get; private set; }
    public DateTimeOffset? OverdueNotifiedForDueDate { get; private set; }
    public DomainTaskItemId? RecurrenceTemplateId { get; private set; }
    public DateTimeOffset? OccurrenceUtc { get; private set; }

    // Foreign keys
    public DomainCategoryId? CategoryId { get; private set; }
    public DomainTaskItemId? ParentTaskItemId { get; private set; }

    // Value objects
    public DateRange DateRange => new() { StartDate = StartDate, DueDate = DueDate };
    public RecurrencePattern? RecurrencePattern { get; private set; }

    // Navigation
    public Category? Category { get; private set; }
    public TaskItem? ParentTaskItem { get; private set; }
    public ICollection<TaskItem> SubTasks { get; private set; } = [];
    public ICollection<Comment> Comments { get; private set; } = [];
    public ICollection<ChecklistItem> ChecklistItems { get; private set; } = [];
    public ICollection<TaskItemTag> TaskItemTags { get; private set; } = [];

    /// <summary>Initializes task item with required dependencies and default state.</summary>
    private TaskItem() { }

    /// <summary>Initializes task item with required dependencies and default state.</summary>
    private TaskItem(DomainTenantId tenantId, string title, string? description, Priority priority, DomainCategoryId? categoryId, DomainTaskItemId? parentTaskItemId, DomainTaskItemId? id)
    {
        if (id.HasValue) Id = id.Value; // D-033: caller-supplied UUIDv7 id makes create idempotent.
        TenantId = tenantId;
        Title = title;
        Description = description;
        Priority = priority;
        Status = TaskItemStatus.Open;
        Features = TaskFeatures.None;
        CategoryId = categoryId;
        ParentTaskItemId = parentTaskItemId;
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public static DomainResult<TaskItem> Create(
        DomainTenantId tenantId, string title, string? description = null,
        Priority priority = Priority.None, DomainCategoryId? categoryId = null,
        DomainTaskItemId? parentTaskItemId = null,
        string? secureDeterministic = null, string? secureRandom = null,
        DomainTaskItemId? id = null)
    {
        var entity = new TaskItem(tenantId, title, description, priority, categoryId, parentTaskItemId, id)
        {
            SecureDeterministic = secureDeterministic,
            SecureRandom = secureRandom
        };
        var validated = entity.Valid();
        if (validated.IsSuccess)
            entity._domainEvents.Raise(new TaskItemCreatedEvent(entity.Id.Value, tenantId.Value, entity.Title));
        return validated;
    }

    /// <summary>
    /// Applies a partial update. Null means "leave current value"; Guid.Empty clears optional
    /// category and parent links for DTO-driven updates.
    /// </summary>
    public DomainResult<TaskItem> Update(
        string? title = null, string? description = null,
        Priority? priority = null, TaskFeatures? features = null,
        decimal? estimatedEffort = null, decimal? actualEffort = null,
        DomainCategoryId? categoryId = null, DomainTaskItemId? parentTaskItemId = null,
        string? secureDeterministic = null, string? secureRandom = null)
    {
        if (title is not null) Title = title;
        if (description is not null) Description = description;
        if (priority.HasValue) Priority = priority.Value;
        if (features.HasValue) Features = features.Value;
        if (estimatedEffort.HasValue) EstimatedEffort = estimatedEffort.Value;
        if (actualEffort.HasValue) ActualEffort = actualEffort.Value;
        if (categoryId.HasValue) CategoryId = categoryId.Value.Value == Guid.Empty ? null : categoryId.Value;
        if (parentTaskItemId.HasValue) ParentTaskItemId = parentTaskItemId.Value.Value == Guid.Empty ? null : parentTaskItemId.Value;
        if (secureDeterministic is not null) SecureDeterministic = secureDeterministic;
        if (secureRandom is not null) SecureRandom = secureRandom;
        return Valid();
    }

    /// <summary>
    /// Moves the task through the allowed status state machine and keeps CompletedDate and TerminalAtUtc
    /// aligned with the terminal statuses. TaskItemStatus.None is a reset escape hatch for seed/test data.
    /// </summary>
    public DomainResult<TaskItem> TransitionStatus(TaskItemStatus newStatus)
    {
        if (newStatus == TaskItemStatus.None)
        {
            Status = TaskItemStatus.None;
            CompletedDate = null;
            TerminalAtUtc = null;
            return DomainResult<TaskItem>.Success(this);
        }

        if (!IsValidTransition(Status, newStatus))
            return DomainResult<TaskItem>.Failure($"Cannot transition from {Status} to {newStatus}.");

        var previousStatus = Status;
        var now = DateTimeOffset.UtcNow;
        Status = newStatus;

        if (newStatus == TaskItemStatus.Completed)
            CompletedDate = now;
        else if (previousStatus == TaskItemStatus.Completed)
            CompletedDate = null;

        // Terminal statuses stamp TerminalAtUtc (stale cleanup key); reopening clears it.
        TerminalAtUtc = newStatus is TaskItemStatus.Completed or TaskItemStatus.Cancelled ? now : null;

        _domainEvents.Raise(new TaskItemStatusChangedEvent(Id.Value, TenantId.Value, previousStatus, newStatus));
        if (newStatus == TaskItemStatus.Completed)
            _domainEvents.Raise(new TaskItemCompletedEvent(Id.Value, TenantId.Value, CompletedDate!.Value));

        return DomainResult<TaskItem>.Success(this);
    }

    // D-031 (aggregate-level ETag): every child mutation below calls the base Touch() so EF marks the
    // root Modified and VersionTimestampInterceptor bumps the root Version. Child PUT/DELETE therefore
    // use the root ETag as the If-Match currency; child DTO Version values are display-only.
    #region Child Collection Methods

    /// <summary>
    /// Add a new comment to this task item.
    /// </summary>
    public DomainResult<Comment> AddComment(string body, CommentId? commentId = null)
    {
        var result = Comment.Create(TenantId, Id, body, commentId);
        if (result.IsFailure) return result;

        Comments.Add(result.Value!);
        Touch();
        return result;
    }

    /// <summary>Removes remove comment while keeping aggregate relationship state consistent.</summary>
    public DomainResult RemoveComment(Comment comment)
    {
        Comments.Remove(comment);
        Touch();
        return DomainResult.Success();
    }

    /// <summary>
    /// Removes a comment by id as an idempotent desired-state operation.
    /// </summary>
    public DomainResult RemoveComment(CommentId commentId)
    {
        var toRemove = Comments.FirstOrDefault(c => c.Id == commentId);
        if (toRemove != null) Comments.Remove(toRemove);
        Touch();
        return DomainResult.Success(); // Always return success - desired state (comment removed) is achieved
    }

    /// <summary>
    /// Add a new checklist item to this task item.
    /// </summary>
    public DomainResult<ChecklistItem> AddChecklistItem(string title, int sortOrder = 0, ChecklistItemId? checklistItemId = null)
    {
        var result = ChecklistItem.Create(TenantId, Id, title, sortOrder, checklistItemId);
        if (result.IsFailure) return result;

        ChecklistItems.Add(result.Value!);
        Touch();
        return result;
    }

    /// <summary>Removes remove checklist item while keeping aggregate relationship state consistent.</summary>
    public DomainResult RemoveChecklistItem(ChecklistItem checklistItem)
    {
        ChecklistItems.Remove(checklistItem);
        Touch();
        return DomainResult.Success();
    }

    /// <summary>
    /// Removes a checklist item by id as an idempotent desired-state operation.
    /// </summary>
    public DomainResult RemoveChecklistItem(ChecklistItemId checklistItemId)
    {
        var toRemove = ChecklistItems.FirstOrDefault(ci => ci.Id == checklistItemId);
        if (toRemove != null) ChecklistItems.Remove(toRemove);
        Touch();
        return DomainResult.Success(); // Always return success - desired state is achieved
    }

    /// <summary>
    /// Associate an existing tag with this task item.
    /// </summary>
    public DomainResult<TaskItemTag> AssociateTag(TagId tagId)
    {
        var existing = TaskItemTags.FirstOrDefault(t => t.TagId == tagId);
        if (existing != null) return DomainResult<TaskItemTag>.Success(existing); // Idempotent

        var result = TaskItemTag.Create(TenantId, Id, tagId);
        if (result.IsFailure) return result;

        TaskItemTags.Add(result.Value!);
        Touch();
        return result;
    }

    /// <summary>Removes remove tag while keeping aggregate relationship state consistent.</summary>
    public DomainResult RemoveTag(TaskItemTag taskItemTag)
    {
        TaskItemTags.Remove(taskItemTag);
        Touch();
        return DomainResult.Success();
    }

    /// <summary>
    /// Removes a tag association by tag id as an idempotent desired-state operation.
    /// </summary>
    public DomainResult RemoveTag(TagId tagId)
    {
        var toRemove = TaskItemTags.FirstOrDefault(t => t.TagId == tagId);
        if (toRemove != null) TaskItemTags.Remove(toRemove);
        Touch();
        return DomainResult.Success(); // Always return success - desired state (tag not assigned) is achieved
    }

    /// <summary>
    /// Marks the aggregate changed for a child field update. Add and remove already Touch() through
    /// the methods above; an in-place child edit (comment body, checklist item title) never passes
    /// through the root, so the application layer states it explicitly and the root Version still moves.
    /// </summary>
    public void MarkChildMutated() => Touch();

    #endregion

    /// <summary>
    /// Replaces the scheduling dates. Validation is intentionally outside the value object so
    /// services can decide whether incomplete dates are allowed.
    /// </summary>
    public void UpdateDateRange(DateTimeOffset? startDate, DateTimeOffset? dueDate)
    {
        StartDate = startDate;
        DueDate = dueDate;
    }

    /// <summary>
    /// Replaces or clears the recurrence value object. Schedulers read this as a template
    /// signal; this aggregate does not create recurring child tasks itself.
    /// </summary>
    public void UpdateRecurrencePattern(RecurrencePattern? pattern)
    {
        RecurrencePattern = pattern;
    }

    /// <summary>Checks whether a task status transition is allowed by the domain state machine.</summary>
    private static bool IsValidTransition(TaskItemStatus current, TaskItemStatus target) =>
        (current, target) switch
        {
            (TaskItemStatus.Open, TaskItemStatus.InProgress) => true,
            (TaskItemStatus.Open, TaskItemStatus.Cancelled) => true,
            (TaskItemStatus.InProgress, TaskItemStatus.Completed) => true,
            (TaskItemStatus.InProgress, TaskItemStatus.Blocked) => true,
            (TaskItemStatus.InProgress, TaskItemStatus.Cancelled) => true,
            (TaskItemStatus.Blocked, TaskItemStatus.InProgress) => true,
            (TaskItemStatus.Blocked, TaskItemStatus.Cancelled) => true,
            (TaskItemStatus.Completed, TaskItemStatus.Open) => true,
            (TaskItemStatus.Cancelled, TaskItemStatus.Open) => true,
            _ => false
        };

    /// <summary>Creates a valid task item instance with domain-required defaults.</summary>
    private DomainResult<TaskItem> Valid()
    {
        var errors = new List<DomainError>();
        if (TenantId.Value == Guid.Empty) errors.Add(DomainError.Create("Tenant ID cannot be empty."));
        if (string.IsNullOrWhiteSpace(Title)) errors.Add(DomainError.Create("Title is required."));
        if (Title is not null && Title.Length < DomainConstants.RULE_DEFAULT_NAME_LENGTH_MIN)
            errors.Add(DomainError.Create($"Title must be at least {DomainConstants.RULE_DEFAULT_NAME_LENGTH_MIN} characters."));
        if (ExceedsSecureBudget(SecureDeterministic) || ExceedsSecureBudget(SecureRandom))
            errors.Add(DomainError.Create($"Secure property must not exceed {DomainConstants.RULE_SECURE_PROPERTY_MAX_BYTES} bytes (UTF8)."));
        return errors.Count > 0
            ? DomainResult<TaskItem>.Failure(errors)
            : DomainResult<TaskItem>.Success(this);
    }

    /// <summary>True when a secure property's UTF8 encoding exceeds the plaintext budget (ciphertext column is 256 bytes: 200 + 12 nonce + 16 tag + headroom).</summary>
    private static bool ExceedsSecureBudget(string? value) =>
        value is not null
        && System.Text.Encoding.UTF8.GetByteCount(value) > DomainConstants.RULE_SECURE_PROPERTY_MAX_BYTES;
}
