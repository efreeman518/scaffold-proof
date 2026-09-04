namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>Bounded condition waits; every wait has an explicit deadline and a description used in the failure.</summary>
internal static class Poll
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>Waits for <paramref name="condition"/> to hold, failing the test at the deadline.</summary>
    internal static async Task UntilAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan timeout, string description, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition(ct))
                return;

            await Task.Delay(Interval, ct);
        }

        Assert.Fail($"Timed out after {timeout} waiting for {description}.");
    }

    /// <summary>Waits for a value to satisfy <paramref name="predicate"/> and returns it, failing at the deadline.</summary>
    internal static async Task<T> UntilAsync<T>(Func<CancellationToken, Task<T>> read, Func<T, bool> predicate, TimeSpan timeout, string description, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        T value = await read(ct);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate(value))
                return value;

            await Task.Delay(Interval, ct);
            value = await read(ct);
        }

        Assert.Fail($"Timed out after {timeout} waiting for {description}; last value was {value}.");
        return value;
    }
}
