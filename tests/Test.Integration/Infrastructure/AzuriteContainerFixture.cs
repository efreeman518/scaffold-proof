using EF.IntegrationTesting.Testcontainers;
using Testcontainers.Azurite;
using TaskFlow.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Standalone Azurite Testcontainer for the component tier. Provides a real Table Storage endpoint for
/// the audit-repository test without booting the Aspire AppHost graph. Started once by
/// <see cref="IntegrationTestSetup"/>; <see cref="StartupError"/> is recorded so dependent tests fail with
/// its diagnostics without aborting assembly discovery.
/// </summary>
internal static class AzuriteContainerFixture
{
    // Pass the image explicitly (the parameterless AzuriteBuilder() ctor is obsolete); pin the tag
    // to latest like every other emulator.
    private static readonly ContainerFixture<AzuriteContainer> Azurite =
        new(() => new AzuriteBuilder(ContainerImages.Azurite)
            .WithEnvironment(ContainerImages.AzuriteSkipApiVersionCheckVariable, "true")
            .Build());

    /// <summary>Startup failure recorded by the fixture; null when the container started cleanly.</summary>
    internal static Exception? StartupError => Azurite.StartupError;

    /// <summary>Azurite connection string (blob/queue/table). Only valid once startup succeeded.</summary>
    internal static string ConnectionString => Azurite.Container.GetConnectionString();

    /// <summary>Starts the Azurite container; a post-preflight failure is kept in <see cref="StartupError"/>.</summary>
    internal static Task StartAsync(CancellationToken cancellationToken = default) => Azurite.StartAsync(cancellationToken);

    /// <summary>Disposes the Azurite container.</summary>
    internal static Task StopAsync() => Azurite.DisposeAsync().AsTask();
}
