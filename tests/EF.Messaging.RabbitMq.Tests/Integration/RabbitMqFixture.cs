using Testcontainers.RabbitMq;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>
/// Assembly-scoped RabbitMQ broker for the integration tier. The container starts lazily on first use so the Unit
/// lane never needs a container runtime, and is disposed by <c>[AssemblyCleanup]</c>.
/// </summary>
[TestClass]
public static class RabbitMqFixture
{
    internal const string Image = "rabbitmq:4-management";
    internal const string Username = "ef";
    internal const string Password = "ef-password";
    private const ushort ManagementPort = 15672;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RabbitMqContainer? _container;
    private static string? _unavailableReason;

    /// <summary>Starts the shared broker once and returns it, or reports the container runtime failure.</summary>
    internal static async Task<RabbitMqContainer> EnsureStartedAsync(CancellationToken ct)
    {
        if (_container is not null)
            return _container;

        await Gate.WaitAsync(ct);
        try
        {
            if (_unavailableReason is not null)
                Assert.Inconclusive(_unavailableReason);

            if (_container is not null)
                return _container;

            RabbitMqContainer container = NewBroker().Build();
            try
            {
                await container.StartAsync(ct);
                await WaitForManagementApiAsync(container, ct);
            }
            catch (Exception ex)
            {
                _unavailableReason = $"RabbitMQ container ({Image}) could not start: {ex.Message}";
                Assert.Inconclusive(_unavailableReason);
            }

            _container = container;
            return container;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Builds an independent broker, used by tests that must stop the broker themselves.</summary>
    internal static RabbitMqBuilder NewBroker() => new RabbitMqBuilder(Image)
        .WithUsername(Username)
        .WithPassword(Password)
        .WithPortBinding(ManagementPort, true);

    /// <summary>Management HTTP client for a running broker.</summary>
    internal static RabbitMqManagement Management(RabbitMqContainer container) =>
        new(container.Hostname, container.GetMappedPublicPort(ManagementPort), Username, Password);

    private static async Task WaitForManagementApiAsync(RabbitMqContainer container, CancellationToken ct)
    {
        using RabbitMqManagement management = Management(container);
        await Poll.UntilAsync(
            async token => await management.IsReadyAsync(token),
            TimeSpan.FromSeconds(60),
            "the management API to answer /api/overview",
            ct);
    }

    /// <summary>Disposes the shared broker after the assembly's tests complete.</summary>
    [AssemblyCleanup]
    public static async Task CleanupAsync(TestContext _)
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
