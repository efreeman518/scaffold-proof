using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Domain.Shared.Events;

/// <summary>Provides attachment uploaded event behavior for the shared domain event set.</summary>
public sealed record AttachmentUploadedEvent(
    Guid AttachmentId,
    Guid OwnerId,
    AttachmentOwnerType OwnerType,
    Guid TenantId) : TenantDomainEvent;
