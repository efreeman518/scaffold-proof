using Testcontainers.RabbitMq;
using TaskFlow.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Assembly-scoped RabbitMQ broker for the strict NonAzure component lane. RabbitMQ 4 refuses a remote
/// <c>guest</c> login, so the container gets its own credentials, and the client reads them back out of the
/// container connection string.
/// </summary>
internal static class RabbitMqBrokerFixture
{
    private const string Image = ContainerImages.RabbitMq;
    private const string Username = "taskflow";
    private const string Password = "taskflow-password";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RabbitMqContainer? _container;

    internal static Exception? StartupError { get; private set; }

    internal static RabbitMqContainer Container => _container
        ?? throw new InvalidOperationException("RabbitMQ container has not started.");

    internal static string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("RabbitMQ container has not started.");

    /// <summary>Starts the shared broker once and captures a post-preflight startup failure for dependent tests.</summary>
    internal static async Task StartAsync(CancellationToken ct)
    {
        if (_container is not null || StartupError is not null) return;

        await Gate.WaitAsync(ct);
        try
        {
            if (_container is not null || StartupError is not null) return;

            var container = new RabbitMqBuilder(Image)
                .WithUsername(Username)
                .WithPassword(Password)
                .Build();
            try
            {
                await container.StartAsync(ct);
                _container = container;
            }
            catch (Exception ex)
            {
                StartupError = ex;
                await container.DisposeAsync();
            }
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
