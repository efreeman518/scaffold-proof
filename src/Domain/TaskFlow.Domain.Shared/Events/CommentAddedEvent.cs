namespace TaskFlow.Domain.Shared.Events;

/// <summary>Provides comment added event behavior for the shared domain event set.</summary>
public record CommentAddedEvent(Guid CommentId, Guid TaskItemId, Guid TenantId) : IDomainEvent;
