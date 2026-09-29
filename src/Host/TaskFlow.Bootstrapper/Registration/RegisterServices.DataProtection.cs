using EF.AspNetCore.DataProtection;
using EF.Host;
using EF.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Bootstrapper;

public static partial class RegisterServices
{
    public const string DataProtectionPersistenceConfigKey = HostingLaneResolver.DataProtectionConfigurationKey;
    public const string DataProtectionPersistenceEnvVar = HostingLaneResolver.DataProtectionEnvironmentVariable;

    /// <summary>
    /// Resolves the strict lane's Data Protection persistence (D-060).
    /// </summary>
    public static DataProtectionPersistence ResolveDataProtectionPersistence(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return StrictEnum.Parse<DataProtectionPersistence>(
            HostingLaneResolver.Resolve(config).DataProtection, "Data Protection persistence");
    }

    /// <summary>
    /// Configures the Data Protection key ring through <c>EF.AspNetCore.DataProtection</c> (D-043): the lane picks
    /// the persistence (AzureBlob, Redis or None), and Azure Key Vault encrypts the keys whenever
    /// <c>DataProtectionEncryptionKeyUrl</c> is set, whatever the arm. <c>DataProtection:AzureBlob:ContainerName</c> /
    /// <c>BlobName</c> bind from the package section; the key ring location comes from <c>DataProtectionKeysFileUrl</c>
    /// (full blob URI) or the <c>BlobStorage1</c> endpoint or connection string. The Redis arm opens its own
    /// connection on first key access over <c>Redis1</c> (caching exposes no shared <c>IConnectionMultiplexer</c>).
    /// The framework's implicit application discriminator is kept, so payloads protected before this change still
    /// unprotect.
    /// </summary>
    [ProviderSwitch(typeof(IDataProtectionProvider))]
    public static IServiceCollection AddTaskFlowDataProtection(this IHostApplicationBuilder builder, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var config = builder.Configuration;
        var appName = config.GetValue<string>("AppName") ?? builder.Environment.ApplicationName;
        var env = builder.Environment.EnvironmentName;

        var settings = config.GetSection(DataProtectionSettings.ConfigSectionName).Get<DataProtectionSettings>()
            ?? new DataProtectionSettings();
        settings.Persistence = ResolveDataProtectionPersistence(config);
        settings.KeyVaultKeyUri = config[HostingLaneResolver.DataProtectionEncryptionKeyUrlConfigurationKey];
        switch (settings.Persistence)
        {
            case DataProtectionPersistence.AzureBlob:
                settings.AzureBlob.BlobUri = config["DataProtectionKeysFileUrl"];
                settings.AzureBlob.Connection = config.ResolveConnection(
                    "BlobStorage1", "BlobStorage1", "BlobStorage1:blobServiceUri", "Values:BlobStorage1");
                // Named in TaskFlow's keys; the package's own message names its settings properties instead.
                if (string.IsNullOrWhiteSpace(settings.AzureBlob.BlobUri) && string.IsNullOrWhiteSpace(settings.AzureBlob.Connection))
                    throw new InvalidOperationException(
                        $"{DataProtectionPersistenceConfigKey}=AzureBlob requires DataProtectionKeysFileUrl or the BlobStorage1 endpoint or connection string.");
                break;
            case DataProtectionPersistence.Redis:
                settings.Redis.ConnectionString = config.GetConnectionString("Redis1")
                    ?? throw new InvalidOperationException(
                        $"{DataProtectionPersistenceConfigKey}=Redis requires the Redis1 connection string.");
                break;
        }

        builder.Services.AddEfDataProtection(settings, AzureCredentialFactory.Create(config));

        if (settings.Persistence == DataProtectionPersistence.None)
            logger.DataProtectionPersistenceNone(appName, env);
        else
            logger.ConfigureDataProtectionPersistence(appName, env, settings.Persistence.ToString());

        return builder.Services;
    }
}
