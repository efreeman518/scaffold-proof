using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TaskFlow.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>Standalone MongoDB service for the explicit NonAzure TaskView alternative.</summary>
internal static class MongoDbContainerFixture
{
    private const int MongoPort = 27017;

    private static readonly IContainer MongoDb = new ContainerBuilder(ContainerImages.MongoDb)
        .WithPortBinding(MongoPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(MongoPort))
        .Build();

    internal static Exception? StartupError { get; private set; }

    internal static string ConnectionString => $"mongodb://{MongoDb.Hostname}:{MongoDb.GetMappedPublicPort(MongoPort)}";

    internal static async Task StartAsync()
    {
        try
        {
            await MongoDb.StartAsync();
        }
        catch (Exception ex)
        {
            StartupError = ex;
        }
    }

    internal static async Task StopAsync() => await MongoDb.DisposeAsync();
}
