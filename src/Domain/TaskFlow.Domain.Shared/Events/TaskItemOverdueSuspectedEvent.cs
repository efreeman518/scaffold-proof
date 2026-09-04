namespace TaskFlow.Domain.Shared.Events;

/// <summary>Provides task item overdue suspected event behavior for the shared domain event set.</summary>
public record TaskItemOverdueSuspectedEvent(Guid TaskItemId, Guid TenantId, DateTimeOffset DueDate) : IDomainEvent;
