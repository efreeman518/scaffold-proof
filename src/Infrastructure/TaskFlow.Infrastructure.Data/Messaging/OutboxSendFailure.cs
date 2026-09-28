namespace TaskFlow.Infrastructure.Data.Messaging;

/// <summary>One message of a transport send that the broker did not accept.</summary>
/// <param name="Index">Position in the list passed to <see cref="IIntegrationEventTransport.SendBatchAsync"/>.</param>
/// <param name="Error">Why it failed; recorded on the row.</param>
/// <param name="Permanent">
/// True only when retrying cannot succeed (the message exceeds the broker's size limit). A permanent failure is
/// dead-lettered at once instead of spending the attempt budget; everything else is retried with backoff.
/// </param>
public readonly record struct OutboxSendFailure(int Index, string Error, bool Permanent);
