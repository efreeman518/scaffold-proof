using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Data.Messaging;

/// <summary>
/// Broker port for the outbox dispatcher (D-026, D-034). It is deliberately declared over the persisted
/// <see cref="OutboxMessage"/> row rather than an application type: only the dispatcher calls it, the row id is
/// the broker MessageId, and an application service that wants an event raises it on the aggregate instead.
/// </summary>
public interface IIntegrationEventTransport
{
    /// <summary>False when no broker is configured; the dispatcher then leaves rows in place instead of draining them.</summary>
    bool CanDispatch { get; }

    /// <summary>
    /// Sends one claimed batch to a single destination. Throwing releases the lease so the rows are retried,
    /// so a partial send must throw rather than report success.
    /// </summary>
    /// <param name="destination">Logical channel name from <see cref="OutboxMessage.Destination"/>.</param>
    /// <param name="messages">Claimed rows, all with the same destination.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendBatchAsync(string destination, IReadOnlyList<OutboxMessage> messages, CancellationToken ct);
}
