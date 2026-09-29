using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using EF.IntegrationTesting.Testcontainers;
using TaskFlow.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>Standalone MongoDB service for the explicit NonAzure TaskView alternative.</summary>
internal static class MongoDbContainerFixture
{
    private const int MongoPort = 27017;

    private static readonly ContainerFixture<IContainer> MongoDb = new(() => new ContainerBuilder(ContainerImages.MongoDb)
        .WithPortBinding(MongoPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(MongoPort))
        .Build());

    internal static Exception? StartupError => MongoDb.StartupError;

    internal static string ConnectionString =>
        $"mongodb://{MongoDb.Container.Hostname}:{MongoDb.Container.GetMappedPublicPort(MongoPort)}";

    internal static Task StartAsync(CancellationToken cancellationToken = default) => MongoDb.StartAsync(cancellationToken);

    internal static Task StopAsync() => MongoDb.DisposeAsync().AsTask();
}
