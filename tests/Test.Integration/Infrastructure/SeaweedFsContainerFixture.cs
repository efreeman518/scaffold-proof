using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using EF.IntegrationTesting.Testcontainers;
using TaskFlow.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>Standalone SeaweedFS S3-compatible service for component integration tests.</summary>
internal static class SeaweedFsContainerFixture
{
    private const int S3Port = 8333;
    internal const string AccessKey = "taskflow-development";
    internal const string SecretKey = "taskflow-development-secret";

    private static readonly ContainerFixture<IContainer> SeaweedFs = new(() => new ContainerBuilder(ContainerImages.SeaweedFs)
        .WithCommand("mini", "-dir=/data")
        .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
        .WithPortBinding(S3Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(S3Port))
        .Build());

    internal static Exception? StartupError => SeaweedFs.StartupError;

    internal static string ServiceUrl =>
        $"http://{SeaweedFs.Container.Hostname}:{SeaweedFs.Container.GetMappedPublicPort(S3Port)}";

    internal static Task StartAsync(CancellationToken cancellationToken = default) => SeaweedFs.StartAsync(cancellationToken);

    internal static Task StopAsync() => SeaweedFs.DisposeAsync().AsTask();
}
