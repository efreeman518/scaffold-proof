using EF.Audit.Contracts;
using EF.Storage.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Hosting;
using TaskFlow.Infrastructure.Storage;
using TaskFlow.Infrastructure.Storage.CosmosDb;

namespace Test.Support.Hosting;

/// <summary>
/// Container-free host shape for the in-memory API tiers (endpoint contract, benchmark host) on the default NonAzure
/// lane (D-060). Strict lane registration still requires each lane endpoint, so <see cref="Settings"/> points them at
/// inert values, and <see cref="ReplaceDataPlanes"/> swaps the services that would reach them for in-process ones, so
/// a request touches the API and the in-memory database only - no network, no container runtime.
/// </summary>
public static class InertNonAzureLane
{
    private const string InertS3Endpoint = "http://127.0.0.1:1";

    // The NonAzure Data Protection arm (D-043) opens its Redis connection while the host registers services, so it
    // needs a connection string; abortConnect=false keeps that registration from failing, and the short connect timeout
    // bounds how long every host boot blocks on it (250 ms doubled the endpoint suite's run time). The key ring it would
    // back is replaced by the ephemeral provider below, so nothing ever reads or writes through it.
    private const string InertRedisConnection = "127.0.0.1:1,abortConnect=false,connectTimeout=10";
    private const string InertRabbitMqConnection = "amqp://taskflow:taskflow@127.0.0.1:1/";

    // Caching and the rate limiter would share Redis1; pointed at a name with no connection string they stay
    // in-process.
    private const string NoRedisConnectionName = "InertLaneNoRedis";

    /// <summary>
    /// Registration-time lane settings. Apply them as host settings (<c>UseSetting</c>) as well as test configuration:
    /// ConfigureAppConfiguration sources land after Program.cs has already read them to choose providers.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        [HostingLaneResolver.LaneConfigurationKey] = nameof(HostingLane.NonAzure),
        ["ConnectionStrings:Redis1"] = InertRedisConnection,
        ["CacheSettings:0:RedisConnectionStringName"] = NoRedisConnectionName,
        ["RateLimiting:RedisConnectionStringName"] = NoRedisConnectionName,
        ["Storage:S3:ServiceUrl"] = InertS3Endpoint,
        ["Storage:S3:PublicServiceUrl"] = InertS3Endpoint,
        ["Storage:S3:AccessKeyId"] = "taskflow-inert",
        ["Storage:S3:SecretAccessKey"] = "taskflow-inert-secret",
        ["Messaging:RabbitMq:ConnectionString"] = InertRabbitMqConnection
    };

    /// <summary>Replaces every lane data plane that would leave the process with an in-process implementation.</summary>
    public static void ReplaceDataPlanes(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IDataProtectionProvider>();
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        services.RemoveAll<IObjectStorageRepository>();
        services.AddSingleton<IObjectStorageRepository, NoOpBlobStorageRepository>();
        services.RemoveAll<IAuditLogRepository>();
        services.AddSingleton<IAuditLogRepository, NoOpAuditLogRepository>();
        services.RemoveAll<ITaskViewRepository>();
        services.AddSingleton<ITaskViewRepository, NoOpTaskViewRepository>();
    }
}
