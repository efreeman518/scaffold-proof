using EF.IntegrationTesting.Testcontainers;
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

    private static readonly ContainerFixture<RabbitMqContainer> Broker = new(() => new RabbitMqBuilder(Image)
        .WithUsername(Username)
        .WithPassword(Password)
        .Build());

    internal static Exception? StartupError => Broker.StartupError;

    internal static RabbitMqContainer Container => Broker.Container;

    internal static string ConnectionString => Broker.Container.GetConnectionString();

    /// <summary>Starts the shared broker once; a post-preflight startup failure is kept for dependent tests.</summary>
    internal static Task StartAsync(CancellationToken ct) => Broker.StartAsync(ct);

    /// <summary>Disposes the broker; called from the assembly cleanup that owns every container here.</summary>
    internal static Task StopAsync() => Broker.DisposeAsync().AsTask();
}
