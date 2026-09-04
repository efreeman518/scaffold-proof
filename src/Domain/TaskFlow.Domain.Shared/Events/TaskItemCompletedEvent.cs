namespace TaskFlow.Domain.Shared.Events;

/// <summary>Provides task item completed event behavior for the shared domain event set.</summary>
public record TaskItemCompletedEvent(Guid TaskItemId, Guid TenantId, DateTimeOffset CompletedDate) : IDomainEvent;
