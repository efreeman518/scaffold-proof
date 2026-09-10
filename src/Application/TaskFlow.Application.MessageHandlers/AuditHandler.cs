using EF.Audit.Contracts;
using EF.BackgroundServices.Attributes;
using EF.BackgroundServices.InternalMessageBus;
using EF.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace TaskFlow.Application.MessageHandlers;

/// <summary>
/// Internal message-bus handler that persists EF audit entries to the configured audit store.
/// It supports both required and nullable tenant-id audit shapes emitted by the shared interceptor.
/// </summary>
[ScopedMessageHandler]
public class AuditHandler(
    ILogger<AuditHandler> logger,
    IAuditLogRepository auditLogRepository) :
    IMessageHandler<AuditEntry<string, Guid>>,
    IMessageHandler<AuditEntry<string, Guid?>>
{
    /// <summary>Handles audit requests and returns the application result.</summary>
    public Task HandleAsync(AuditEntry<string, Guid> message, CancellationToken cancellationToken = default)
    {
        return HandleCoreAsync(message, cancellationToken);
    }

    /// <summary>Handles audit requests and returns the application result.</summary>
    public Task HandleAsync(AuditEntry<string, Guid?> message, CancellationToken cancellationToken = default)
    {
        return HandleCoreAsync(message, cancellationToken);
    }

    /// <summary>Provides the handle core operation for audit handler.</summary>
    private async Task HandleCoreAsync<TTenantId>(AuditEntry<string, TTenantId> message, CancellationToken cancellationToken)
    {
        await auditLogRepository.AppendAsync(message, cancellationToken).ConfigureAwait(false);

        logger.AuditMessagePersisted(message.Id, message.EntityType, message.Action);
    }
}
