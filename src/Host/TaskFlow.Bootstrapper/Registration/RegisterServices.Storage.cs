using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Storage.S3;

namespace TaskFlow.Bootstrapper;

/// <summary>Object-storage backend selected for this deployment (D-037).</summary>
public enum StorageProvider
{
    /// <summary>Azure Blob Storage behind the unchanged <see cref="IBlobStorageRepository"/>.</summary>
    AzureBlob,

    /// <summary>S3-compatible storage (MinIO locally/on the VPS, any S3 provider in production).</summary>
    S3
}

public static partial class RegisterServices
{
    public const string StorageProviderConfigKey = "Storage:Provider";
    public const string StorageProviderEnvVar = "TASKFLOW_STORAGE_PROVIDER";

    /// <summary>
    /// Resolves the object-storage backend. The environment variable wins over configuration; when neither
    /// is set, the Portable lane defaults to S3 and the Azure lane keeps today's Azure Blob default (D-035).
    /// </summary>
    public static StorageProvider ResolveStorageProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var value = Environment.GetEnvironmentVariable(StorageProviderEnvVar) ?? config[StorageProviderConfigKey];
        if (!string.IsNullOrWhiteSpace(value)) return ParseStorageProvider(value);

        return HostingLaneSelector.Resolve(config) == HostingLane.Portable
            ? StorageProvider.S3
            : StorageProvider.AzureBlob;
    }

    private static StorageProvider ParseStorageProvider(string value) =>
        Enum.TryParse<StorageProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown storage provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<StorageProvider>())}.");

    /// <summary>Dispatches to the selected object-storage backend.</summary>
    [ProviderSwitch(typeof(IBlobStorageRepository))]
    private static void AddStorageServices(IServiceCollection services, IConfiguration config)
    {
        switch (ResolveStorageProvider(config))
        {
            case StorageProvider.AzureBlob:
                AddBlobStorageServices(services, config);
                break;
            case StorageProvider.S3:
                AddS3StorageServices(services, config);
                break;
        }
    }

    /// <summary>
    /// Registers the S3-compatible object-storage arm (D-037). <see cref="S3StorageSettings.PublicServiceUrl"/>
    /// is required and validated eagerly here (not deferred to <c>ValidateOnStart</c>): presigned download
    /// URLs are host-bound, so a missing public endpoint is a configuration error the moment this arm is
    /// selected, not a surprise on the first attachment download.
    /// Internal (not private) so Test.Unit can exercise the fail-fast and the DI registration directly
    /// (<c>TaskFlow.Bootstrapper</c> grants it <c>InternalsVisibleTo</c>).
    /// </summary>
    internal static void AddS3StorageServices(IServiceCollection services, IConfiguration config)
    {
        var settings = config.GetSection(S3StorageSettings.ConfigSectionName).Get<S3StorageSettings>()
            ?? new S3StorageSettings();

        if (string.IsNullOrWhiteSpace(settings.PublicServiceUrl))
            throw new InvalidOperationException(
                $"{StorageProviderConfigKey}=S3 requires {S3StorageSettings.ConfigSectionName}:PublicServiceUrl " +
                "(presigned download URLs are signed against a host, so a public, browser-reachable endpoint must be configured).");

        // AWS SDK types stay out of this assembly (Test.Architecture: only TaskFlow.Infrastructure.Storage
        // may reference Amazon.*); the client/repository wiring lives in the Infrastructure.Storage extension.
        services.AddS3ObjectStorage(settings);
    }
}
