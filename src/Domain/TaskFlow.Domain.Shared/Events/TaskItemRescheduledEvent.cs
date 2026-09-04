namespace TaskFlow.Domain.Shared.Events;

/// <summary>Provides task item rescheduled event behavior for the shared domain event set.</summary>
public record TaskItemRescheduledEvent(
    Guid TaskItemId,
    Guid TenantId,
    DateTimeOffset? NewStartDate,
    DateTimeOffset? NewDueDate) : IDomainEvent;
