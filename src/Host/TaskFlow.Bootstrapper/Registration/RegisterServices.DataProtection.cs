using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TaskFlow.Bootstrapper;

/// <summary>Data Protection key-ring persistence selected for this deployment (D-043).</summary>
public enum DataProtectionPersistence
{
    /// <summary>Azure Blob Storage (today's only persistence option).</summary>
    AzureBlob,

    /// <summary>StackExchange.Redis over the existing <c>Redis1</c> connection (Portable lane).</summary>
    Redis,

    /// <summary>No persistence: the ephemeral in-memory ring. Cursor tokens do not survive a restart or reach other replicas.</summary>
    None
}

public static partial class RegisterServices
{
    public const string DataProtectionPersistenceConfigKey = "DataProtection:Persistence";
    public const string DataProtectionPersistenceEnvVar = "TASKFLOW_DATAPROTECTION_PERSISTENCE";

    /// <summary>
    /// Resolves an explicit persistence selection. Null means "unset": the caller derives today's default
    /// (AzureBlob when <c>DataProtectionKeysFileUrl</c> is configured, else None) or the Portable lane
    /// default (Redis) itself (D-035).
    /// </summary>
    public static DataProtectionPersistence? ResolveDataProtectionPersistence(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var value = Environment.GetEnvironmentVariable(DataProtectionPersistenceEnvVar) ?? config[DataProtectionPersistenceConfigKey];
        if (!string.IsNullOrWhiteSpace(value)) return ParseDataProtectionPersistence(value);

        return HostingLaneSelector.Resolve(config) == HostingLane.Portable
            ? DataProtectionPersistence.Redis
            : null;
    }

    private static DataProtectionPersistence ParseDataProtectionPersistence(string value) =>
        Enum.TryParse<DataProtectionPersistence>(value, ignoreCase: true, out var persistence)
            ? persistence
            : throw new ArgumentException(
                $"Unknown Data Protection persistence '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<DataProtectionPersistence>())}.");

    /// <summary>
    /// Configures the Data Protection key ring: persistence (AzureBlob/Redis/None, D-043) plus key
    /// encryption via Azure Key Vault whenever <c>DataProtectionEncryptionKeyUrl</c> is set, independent of
    /// the chosen persistence arm. Lifted out of TaskFlow.Api's <c>Program.cs</c> (together with
    /// <see cref="CreateAzureCredential"/>) so Gateway and Scheduler can share the same wiring instead of
    /// duplicating it.
    /// </summary>
    [ProviderSwitch(typeof(IDataProtectionProvider))]
    public static IServiceCollection AddTaskFlowDataProtection(this IHostApplicationBuilder builder, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var config = builder.Configuration;
        var services = builder.Services;
        var appName = config.GetValue<string>("AppName") ?? builder.Environment.ApplicationName;
        var env = builder.Environment.EnvironmentName;

        var keysFileUrl = config.GetValue<string?>("DataProtectionKeysFileUrl", null);
        var encryptionKeyUrl = config.GetValue<string?>("DataProtectionEncryptionKeyUrl", null);

        var persistence = ResolveDataProtectionPersistence(config)
            ?? (!string.IsNullOrEmpty(keysFileUrl) ? DataProtectionPersistence.AzureBlob : DataProtectionPersistence.None);

        var dpBuilder = services.AddDataProtection();

        switch (persistence)
        {
            case DataProtectionPersistence.AzureBlob:
                if (string.IsNullOrEmpty(keysFileUrl))
                    throw new InvalidOperationException(
                        $"{DataProtectionPersistenceConfigKey}=AzureBlob requires DataProtectionKeysFileUrl.");
                logger.ConfigureDataProtectionPersistence(appName, env, nameof(DataProtectionPersistence.AzureBlob));
                dpBuilder.PersistKeysToAzureBlobStorage(new Uri(keysFileUrl), CreateAzureCredential(config));
                break;

            case DataProtectionPersistence.Redis:
                var redisConnStr = config.GetConnectionString("Redis1")
                    ?? throw new InvalidOperationException(
                        $"{DataProtectionPersistenceConfigKey}=Redis requires the Redis1 connection string.");
                logger.ConfigureDataProtectionPersistence(appName, env, nameof(DataProtectionPersistence.Redis));
                // shortcut: dedicated eager connection (the package only overloads IConnectionMultiplexer
                // directly or Func<IDatabase>, no Func<IConnectionMultiplexer>). RegisterCachingServices
                // (TaskFlow.Infrastructure.Caching/RegisterCachingServices.cs:70-84) builds its own Redis
                // connections internally via FusionCache's RedisCache/RedisBackplane wrappers and never
                // exposes a shared IConnectionMultiplexer in DI, so there is nothing to reuse today. Upgrade
                // to sharing one multiplexer if/when caching registers IConnectionMultiplexer itself.
                dpBuilder.PersistKeysToStackExchangeRedis(ConnectionMultiplexer.Connect(redisConnStr));
                break;

            case DataProtectionPersistence.None:
                logger.DataProtectionPersistenceNone(appName, env);
                break;
        }

        if (!string.IsNullOrEmpty(encryptionKeyUrl))
            dpBuilder.ProtectKeysWithAzureKeyVault(new Uri(encryptionKeyUrl), CreateAzureCredential(config));

        return services;
    }

    /// <summary>Builds the Azure credential shared by Data Protection and any other Azure-identity consumer.</summary>
    public static DefaultAzureCredential CreateAzureCredential(IConfiguration config)
    {
        var options = new DefaultAzureCredentialOptions();
        var managedIdentityClientId = config.GetValue<string?>("ManagedIdentityClientId", null);
        if (managedIdentityClientId is not null)
            options.ManagedIdentityClientId = managedIdentityClientId;
        var sharedTokenCacheTenantId = config.GetValue<string?>("SharedTokenCacheTenantId", null);
        if (sharedTokenCacheTenantId is not null)
            options.SharedTokenCacheTenantId = sharedTokenCacheTenantId;
        return new DefaultAzureCredential(options);
    }
}
