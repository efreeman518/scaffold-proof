using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Infrastructure.Storage.S3;

/// <summary>
/// DI wiring for the S3 object-storage arm (D-037). Kept inside <c>TaskFlow.Infrastructure.Storage</c> -
/// the only assembly Test.Architecture allows to reference the AWS SDK - so <c>TaskFlow.Bootstrapper</c>'s
/// S3 arm (<c>RegisterServices.AddS3StorageServices</c>) can wire this up after its own config-only
/// validation without importing any <c>Amazon.*</c> type itself.
/// </summary>
public static class S3ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the S3-backed <see cref="IBlobStorageRepository"/>, its bucket provisioner, and the two
    /// <see cref="IAmazonS3"/> clients it needs: one against <see cref="S3StorageSettings.ServiceUrl"/> for
    /// CRUD, one keyed <see cref="S3ObjectStorageRepository.PublicClientKey"/> against
    /// <see cref="S3StorageSettings.PublicServiceUrl"/> for presigned-URL signing (SigV4 signs the Host
    /// header, so the two cannot share a client).
    /// </summary>
    public static IServiceCollection AddS3ObjectStorage(this IServiceCollection services, S3StorageSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton<IAmazonS3>(_ => CreateS3Client(settings, settings.ServiceUrl));
        services.AddKeyedSingleton<IAmazonS3>(
            S3ObjectStorageRepository.PublicClientKey,
            (_, _) => CreateS3Client(settings, settings.PublicServiceUrl));

        services.AddScoped<IBlobStorageRepository, S3ObjectStorageRepository>();
        services.AddSingleton<IS3BucketProvisioner, S3BucketProvisioner>();

        return services;
    }

    /// <summary>
    /// Builds an S3 client against <paramref name="serviceUrl"/>. An empty <paramref name="serviceUrl"/>
    /// targets real AWS S3, resolved from <see cref="S3StorageSettings.Region"/> instead of a custom endpoint.
    /// </summary>
    private static AmazonS3Client CreateS3Client(S3StorageSettings settings, string? serviceUrl)
    {
        var config = new AmazonS3Config { ForcePathStyle = settings.ForcePathStyle };
        if (string.IsNullOrWhiteSpace(serviceUrl))
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(settings.Region);
        else
        {
            config.ServiceURL = serviceUrl;
            config.AuthenticationRegion = settings.Region;
        }

        // Static keys; instance-profile/IRSA credentials replace them on cloud VPS providers without a
        // code change here - only the settings source (env/config) changes.
        return new AmazonS3Client(new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey), config);
    }
}
