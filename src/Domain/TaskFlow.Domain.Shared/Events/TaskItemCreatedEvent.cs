namespace TaskFlow.Domain.Shared.Events;

/// <summary>Provides task item created event behavior for the shared domain event set.</summary>
public sealed record TaskItemCreatedEvent(Guid TaskItemId, Guid TenantId, string Title) : TenantDomainEvent;
