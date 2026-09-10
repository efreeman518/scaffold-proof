using EF.Common.Contracts;
using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// Declares the TaskFlow topology once at startup, serialized across replicas by the D-052 lock.
/// <para>
/// This replaces the package's own topology startup rather than extending it, because the lock belongs to
/// TaskFlow: the package declares whatever topology it is handed and has no business knowing about Redis.
/// </para>
/// <para>
/// The lock serializes rather than deduplicates, and every replica still declares. Declaration is
/// idempotent, so a second identical declaration is free - what is not free is two replicas declaring
/// concurrently while the definitions differ across a rolling deploy, which closes the channel with
/// PRECONDITION_FAILED. Declaring on every replica is also what makes it safe to start consuming: a replica
/// that skipped would subscribe to a queue it never confirmed exists.
/// </para>
/// </summary>
internal sealed class TaskFlowRabbitMqTopologyStartup(
    RabbitMqTopology topology,
    IRabbitMqTopologyDeclarer declarer,
    IDistributedLock distributedLock,
    ILogger<TaskFlowRabbitMqTopologyStartup> logger) : IHostedService
{
    private const string LockKey = "taskflow:rabbitmq-topology";

    /// <summary>Comfortably longer than declaring a handful of exchanges, queues and bindings.</summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for another replica's declaration before declaring anyway.</summary>
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var lease = await AcquireWithinBudgetAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await declarer.DeclareAsync(topology, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Polls until the lock is free or the budget runs out. A timeout declares without the lock rather than
    /// failing startup: an undeclared queue breaks the consumer that subscribes next, and a stuck lock holder
    /// must not be able to keep a replica from starting.
    /// </summary>
    private async Task<IAsyncDisposable?> AcquireWithinBudgetAsync(CancellationToken ct)
    {
        var lease = await distributedLock.TryAcquireAsync(LockKey, LockTtl, ct).ConfigureAwait(false);
        if (lease is not null) return lease;

        logger.TopologyLockDeferred(LockKey);
        var deadline = DateTimeOffset.UtcNow + WaitBudget;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);

            lease = await distributedLock.TryAcquireAsync(LockKey, LockTtl, ct).ConfigureAwait(false);
            if (lease is not null) return lease;
        }

        logger.TopologyLockTimedOut(LockKey, (int)WaitBudget.TotalSeconds);
        return null;
    }
}

/// <summary>Source-generated logging for the RabbitMQ topology startup.</summary>
internal static partial class RabbitMqTopologyLog
{
    /// <summary>Logs that another replica holds the topology lock.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureMessagingRabbitMqBase + 1, Level = LogLevel.Information, Message = "Topology lock {LockKey} held elsewhere; waiting before declaring.")]
    public static partial void TopologyLockDeferred(this ILogger logger, string lockKey);

    /// <summary>Logs that the topology lock never came free and declaration went ahead regardless.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureMessagingRabbitMqBase + 2, Level = LogLevel.Warning, Message = "Topology lock {LockKey} still held after {WaitSeconds}s; declaring without it.")]
    public static partial void TopologyLockTimedOut(this ILogger logger, string lockKey, int waitSeconds);
}
