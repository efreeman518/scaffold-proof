namespace TaskFlow.Application.MessageHandlers.Consumers;

/// <summary>
/// Timing of the D-029 two-state inbox claim, bound from <see cref="ConfigSectionName"/>. The design has to work
/// on RabbitMQ first: a crashed consumer's unacked message is redelivered at once, and every thrown delivery is
/// requeued immediately and counted against <c>MaxDeliveryCount</c>. So the claim lease is short and renewed by
/// the live holder, and a redelivery that meets a live claim waits about one lease for it to resolve before it
/// throws - a crashed holder stops renewing, its claim expires within one lease, and the waiter takes it over
/// instead of burning its delivery budget in milliseconds. Holding a delivery for about one lease stays well
/// under RabbitMQ's <c>consumer_timeout</c> (30 min default) and the Service Bus lock (5 min, auto-renewed).
/// </summary>
public sealed class InboxClaimOptions
{
    /// <summary>Configuration section the options bind from.</summary>
    public const string ConfigSectionName = "Messaging:Inbox";

    /// <summary>
    /// How long a claim stays in progress without renewal. The holder renews every third of it, so a live
    /// holder keeps its claim however long the handler runs; a crashed one loses it within this span.
    /// </summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a delivery waiting on another delivery's claim re-reads it.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Extra wait beyond one <see cref="ClaimLease"/> before a waiting delivery gives up and throws, covering the
    /// last renewal write and clock skew between replicas.
    /// </summary>
    public TimeSpan WaitMargin { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Renewal period: a third of the lease, so two consecutive failed renewals still leave headroom.</summary>
    public TimeSpan RenewalInterval => ClaimLease / 3;

    /// <summary>Longest a delivery waits on a live foreign claim: one lease plus the margin.</summary>
    public TimeSpan WaitBound => ClaimLease + WaitMargin;

    /// <summary>True when the timings are usable: positive, and a poll shorter than the lease.</summary>
    public bool IsValid() =>
        ClaimLease > TimeSpan.Zero
        && PollInterval > TimeSpan.Zero && PollInterval < ClaimLease
        && WaitMargin >= TimeSpan.Zero;
}
