using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Bootstrapper;

/// <summary>Object-storage backend selected for this deployment (D-037).</summary>
public enum StorageProvider
{
    /// <summary>Azure Blob Storage behind the unchanged <see cref="IBlobStorageRepository"/>.</summary>
    AzureBlob,

    /// <summary>S3-compatible storage (MinIO locally/on the VPS, any S3 provider in production). Not implemented yet (slice P4).</summary>
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
                throw new NotSupportedException("Storage provider S3 is not implemented yet (slice P4).");
        }
    }
}
