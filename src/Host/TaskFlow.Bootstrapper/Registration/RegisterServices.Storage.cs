using EF.Storage.Contracts;
using EF.Storage.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TaskFlow.Bootstrapper;

/// <summary>Object-storage backend selected for this deployment (D-037).</summary>
public enum StorageProvider
{
    /// <summary>Azure Blob Storage behind <see cref="IObjectStorageRepository"/>.</summary>
    AzureBlob,

    /// <summary>S3-compatible storage (SeaweedFS locally/on the VPS, any S3 provider in production).</summary>
    S3
}

public static partial class RegisterServices
{
    public const string StorageProviderConfigKey = HostingLaneResolver.StorageConfigurationKey;
    public const string StorageProviderEnvVar = HostingLaneResolver.StorageEnvironmentVariable;

    /// <summary>
    /// Resolves the strict lane's object-storage backend through the shared D-060 contract.
    /// </summary>
    public static StorageProvider ResolveStorageProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ParseStorageProvider(HostingLaneResolver.Resolve(config).Storage);
    }

    private static StorageProvider ParseStorageProvider(string value) =>
        Enum.TryParse<StorageProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown storage provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<StorageProvider>())}.");

    /// <summary>Dispatches to the selected object-storage backend.</summary>
    [ProviderSwitch(typeof(IObjectStorageRepository))]
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
    /// Registers the S3-compatible object-storage arm (D-037) from <c>EF.Storage.S3</c>.
    /// <see cref="S3StorageSettings.PublicServiceUrl"/>
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

        // EF.Storage.S3 owns the two IAmazonS3 clients, the repository, and the bucket provisioner; no
        // Amazon.* type appears in this assembly's own signatures (Test.Architecture asserts that).
        services.AddS3ObjectStorage(settings);
    }
}
