using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;

namespace TaskFlow.Application.MessageHandlers.Consumers;

/// <summary>
/// Transport-independent parse of a message body into an envelope (D-034). Both the Service Bus triggers and
/// the RabbitMQ handlers use this so a malformed message is judged identically on either provider: a body that
/// cannot be understood is never retried, because the next delivery would fail the same way.
/// </summary>
public static class IntegrationEnvelopeReader
{
    /// <summary>Reason recorded when the body is not a valid envelope.</summary>
    public const string MalformedReason = "MalformedEnvelope";

    /// <summary>Reason recorded when the envelope is valid but this build has no consumer for its type.</summary>
    public const string UnsupportedReason = "UnsupportedEventType";

    /// <summary>Parses a message body and checks the event type is one this build knows.</summary>
    /// <param name="body">Raw message body.</param>
    /// <param name="envelope">Parsed envelope, or null when the read failed.</param>
    /// <param name="failure">Dead-letter reason, or null on success.</param>
    /// <returns>True when the envelope is usable.</returns>
    public static bool TryRead(ReadOnlySpan<byte> body, out IntegrationEventEnvelope? envelope, out string? failure)
    {
        envelope = null;
        failure = null;

        try
        {
            envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope>(body);
        }
        catch (JsonException)
        {
            failure = MalformedReason;
            return false;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.Type) || envelope.Id == Guid.Empty)
        {
            envelope = null;
            failure = MalformedReason;
            return false;
        }

        if (!IntegrationEventEnvelope.IsKnownType(envelope.Type))
        {
            envelope = null;
            failure = UnsupportedReason;
            return false;
        }

        return true;
    }
}
