using Microsoft.Extensions.Logging;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Fallback transport for a local run with no broker configured. <see cref="CanDispatch"/> is false, so the
/// dispatcher never claims: rows accumulate in the outbox table (durable, replayable) instead of being silently
/// dropped, which is what a no-op publisher used to do.
/// </summary>
public sealed class NoOpEventTransport(ILogger<NoOpEventTransport> logger) : IIntegrationEventTransport
{
    /// <inheritdoc />
    public bool CanDispatch => false;

    /// <inheritdoc />
    public Task SendBatchAsync(string destination, IReadOnlyList<OutboxMessage> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        logger.NoOpTransport(destination, messages.Count);
        return Task.CompletedTask;
    }
}
