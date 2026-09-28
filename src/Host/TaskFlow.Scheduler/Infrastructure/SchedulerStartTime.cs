namespace TaskFlow.Scheduler.Infrastructure;

/// <summary>
/// When this Scheduler host started, recorded by the host itself. The scheduler health check needs it to tell
/// "no cron job has fired yet because the host just started" from "no cron job has fired since the host started
/// long ago" - the second is exactly the silent failure the check exists to catch.
/// </summary>
public sealed class SchedulerStartTime(TimeProvider timeProvider) : IHostedService
{
    private long _startedAtTicks = -1;

    /// <summary>The host start time, or null before the host has started.</summary>
    public DateTimeOffset? StartedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _startedAtTicks);
            return ticks < 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.CompareExchange(ref _startedAtTicks, timeProvider.GetUtcNow().UtcTicks, -1);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
