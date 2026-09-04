using Microsoft.Extensions.Options;

namespace EF.Messaging.RabbitMq.Tests;

/// <summary>Minimal <see cref="IOptionsMonitor{TOptions}"/> over a fixed instance; no change notifications.</summary>
internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
