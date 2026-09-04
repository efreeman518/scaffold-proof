using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Interceptors;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// D-026 staging for paths with no tracked aggregate to raise the event (bulk <c>ExecuteUpdate</c> jobs).
/// The row joins the caller's unit of work and is written by the caller's own <c>SaveChangesAsync</c>.
/// </summary>
public sealed class OutboxStaging(TaskFlowDbContextTrxn db, TimeProvider? timeProvider = null) : IOutboxStaging
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public void Stage(IntegrationEventEnvelope envelope, Guid? deterministicId = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var staged = deterministicId is null ? envelope : envelope with { Id = deterministicId.Value };
        db.OutboxMessages.Add(OutboxStagingInterceptor.ToRow(staged, _timeProvider.GetUtcNow()));
    }
}
