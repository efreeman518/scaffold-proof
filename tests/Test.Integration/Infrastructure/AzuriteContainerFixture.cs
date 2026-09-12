using Testcontainers.Azurite;
using TaskFlow.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Standalone Azurite Testcontainer for the component tier. Provides a real Table Storage endpoint for
/// the audit-repository test without booting the Aspire AppHost graph. Started once by
/// <see cref="IntegrationTestSetup"/>; <see cref="StartupError"/> is captured so dependent tests fail with
/// its diagnostics without aborting assembly discovery.
/// </summary>
internal static class AzuriteContainerFixture
{
    // Pass the image explicitly (the parameterless AzuriteBuilder() ctor is obsolete); pin the tag
    // to latest like every other emulator.
    private static readonly AzuriteContainer Azurite =
        new AzuriteBuilder(ContainerImages.Azurite).Build();

    /// <summary>Startup failure captured by <see cref="StartAsync"/>; null when the container started cleanly.</summary>
    internal static Exception? StartupError { get; private set; }

    /// <summary>Azurite connection string (blob/queue/table). Only valid once startup succeeded.</summary>
    internal static string ConnectionString => Azurite.GetConnectionString();

    /// <summary>Starts the Azurite container, capturing any post-preflight failure for dependent tests.</summary>
    internal static async Task StartAsync()
    {
        try
        {
            await Azurite.StartAsync();
        }
        catch (Exception ex)
        {
            StartupError = ex;
        }
    }

    /// <summary>Disposes the Azurite container.</summary>
    internal static async Task StopAsync() => await Azurite.DisposeAsync();
}
