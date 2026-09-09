namespace TaskFlow.Domain.Shared.Events;

/// <summary>
/// Raised when a task's embeddable content - its title or description - actually changed value. Status,
/// priority, effort and link edits do not raise it: the embedding pipeline (D-040) is the only consumer and
/// re-embedding a task whose text is identical would cost a model call and rewrite the same vector.
/// </summary>
/// <param name="TaskItemId">Task whose content changed.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="OccurredAtUtc">When the content changed, taken from the aggregate rather than the dispatcher.</param>
public record TaskItemContentChangedEvent(
    Guid TaskItemId,
    Guid TenantId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
