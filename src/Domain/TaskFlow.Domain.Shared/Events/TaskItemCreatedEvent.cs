namespace TaskFlow.Domain.Shared.Events;

/// <summary>Provides task item created event behavior for the shared domain event set.</summary>
public record TaskItemCreatedEvent(Guid TaskItemId, Guid TenantId, string Title) : IDomainEvent;
