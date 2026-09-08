using Testcontainers.Minio;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Standalone MinIO Testcontainer for the component tier. Provides a real S3-compatible endpoint for the
/// D-037 S3 object-storage arm without booting the Aspire AppHost graph. Started once by
/// <see cref="IntegrationTestSetup"/>; <see cref="StartupError"/> is captured so dependent tests fail with
/// its diagnostics without aborting assembly discovery.
/// </summary>
internal static class MinioContainerFixture
{
    private static readonly MinioContainer Minio = new MinioBuilder("minio/minio:latest").Build();

    /// <summary>Startup failure captured by <see cref="StartAsync"/>; null when the container started cleanly.</summary>
    internal static Exception? StartupError { get; private set; }

    /// <summary>
    /// Endpoint the mapped container port resolves to. Under a run-scoped <c>TESTCONTAINERS_HOST_OVERRIDE</c>
    /// (Podman WSL2 does not forward container ports to localhost) this already reflects the override host,
    /// so it doubles as the S3 arm's required <c>Storage:S3:PublicServiceUrl</c> in tests: there is no
    /// separate in-network host here, unlike the Compose deployment where the app reaches MinIO by service
    /// name and a browser reaches it through Caddy.
    /// </summary>
    internal static string ServiceUrl => Minio.GetConnectionString();

    internal static string AccessKey => Minio.GetAccessKey();

    internal static string SecretKey => Minio.GetSecretKey();

    /// <summary>Starts the MinIO container, capturing any post-preflight failure for dependent tests.</summary>
    internal static async Task StartAsync()
    {
        try
        {
            await Minio.StartAsync();
        }
        catch (Exception ex)
        {
            StartupError = ex;
        }
    }

    /// <summary>Disposes the MinIO container.</summary>
    internal static async Task StopAsync() => await Minio.DisposeAsync();
}
