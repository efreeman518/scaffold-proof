namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// Stages an integration event on the current unit of work for paths that have no tracked aggregate to raise it
/// (bulk <c>ExecuteUpdate</c> jobs). The row is written by the caller's own <c>SaveChangesAsync</c>, so it is
/// still transactional with the change it describes (D-026).
/// </summary>
public interface IOutboxStaging
{
    /// <summary>
    /// Adds one outbox row to the write context without saving.
    /// </summary>
    /// <param name="envelope">The envelope to persist; its <c>Id</c> becomes the outbox row id and the MessageId.</param>
    /// <param name="deterministicId">
    /// Overrides the envelope id so a job that re-runs stages the same MessageId (for example a UUIDv5 of
    /// tenant + entity + occurrence), which the ConsumerInbox then rejects as a duplicate.
    /// </param>
    void Stage(IntegrationEventEnvelope envelope, Guid? deterministicId = null);
}
