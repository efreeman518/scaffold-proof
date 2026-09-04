using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Domain.Shared.Events;

/// <summary>Provides task item status changed event behavior for the shared domain event set.</summary>
public record TaskItemStatusChangedEvent(
    Guid TaskItemId,
    Guid TenantId,
    TaskItemStatus OldStatus,
    TaskItemStatus NewStatus) : IDomainEvent;
