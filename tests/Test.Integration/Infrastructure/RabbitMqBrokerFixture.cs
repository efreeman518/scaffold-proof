using Testcontainers.RabbitMq;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Lazily started RabbitMQ broker for the D-034 transport tests. Started on first use rather than in
/// <c>AssemblyInitialize</c> so the database-only tests in this assembly do not pay for a broker they never use.
/// RabbitMQ 4 refuses a remote <c>guest</c> login, so the container gets its own credentials, and the client
/// reads them back out of the container connection string.
/// </summary>
internal static class RabbitMqBrokerFixture
{
    private const string Image = "rabbitmq:4-management";
    private const string Username = "taskflow";
    private const string Password = "taskflow-password";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RabbitMqContainer? _container;
    private static string? _unavailableReason;

    /// <summary>Starts the shared broker once, or reports why the container runtime could not provide one.</summary>
    internal static async Task<RabbitMqContainer> EnsureStartedAsync(CancellationToken ct)
    {
        if (_container is not null) return _container;

        await Gate.WaitAsync(ct);
        try
        {
            if (_unavailableReason is not null) Assert.Inconclusive(_unavailableReason);
            if (_container is not null) return _container;

            var container = new RabbitMqBuilder(Image)
                .WithUsername(Username)
                .WithPassword(Password)
                .Build();
            try
            {
                await container.StartAsync(ct);
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

    /// <summary>Disposes the broker; called from the assembly cleanup that owns every container here.</summary>
    internal static async Task StopAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
