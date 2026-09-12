using EF.Audit.Contracts;
using EF.Storage.Contracts;
using EF.Storage.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using TaskFlow.Hosting;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Bootstrapper;
using Microsoft.Extensions.Options;
using TaskFlow.Infrastructure.Repositories;
using TaskFlow.Infrastructure.Storage;

namespace Test.Unit.Hosting;

/// <summary>
/// Selector-table coverage for the D-035..D-045 provider switches owned by TaskFlow.Bootstrapper: every
/// switch resolves explicit env var over explicit config key over lane default over hard default, and an
/// unrecognized value fails fast instead of silently falling back.
/// Pure-unit tier (in-memory IConfiguration): env-var cases are marked <see cref="DoNotParallelizeAttribute"/>
/// because <see cref="Environment.SetEnvironmentVariable(string, string?)"/> is process-global; every other
/// case (including the lane itself) is driven through configuration so it stays parallel-safe.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ProviderSwitchSelectorTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    // ----- HostingLane -----

    [TestMethod]
    public void ResolveHostingLane_Unset_DefaultsToAzure() =>
        Assert.AreEqual(HostingLane.Azure, HostingLaneResolver.ResolveLane(Config()));

    [TestMethod]
    public void ResolveHostingLane_ConfigPortable_ReturnsNonAzure() =>
        Assert.AreEqual(HostingLane.NonAzure, HostingLaneResolver.ResolveLane(
            Config((HostingLaneResolver.LaneConfigurationKey, "Portable"))));

    [TestMethod]
    public void ResolveHostingLane_UnknownValue_Throws()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() =>
            HostingLaneResolver.ResolveLane(Config((HostingLaneResolver.LaneConfigurationKey, "OnPrem"))));
        StringAssert.Contains(ex.Message, "Azure");
        StringAssert.Contains(ex.Message, "Portable");
    }

    [TestMethod]
    [DoNotParallelize]
    public void ResolveHostingLane_EnvWinsOverConfig()
    {
        WithEnv(HostingLaneResolver.LaneEnvironmentVariable, "Portable", () =>
            Assert.AreEqual(
                HostingLane.NonAzure,
                HostingLaneResolver.ResolveLane(Config((HostingLaneResolver.LaneConfigurationKey, "Azure")))));
    }

    // ----- Storage -----

    [TestMethod]
    public void ResolveStorageProvider_Unset_AzureLane_DefaultsToAzureBlob() =>
        Assert.AreEqual(StorageProvider.AzureBlob, RegisterServices.ResolveStorageProvider(Config()));

    [TestMethod]
    public void ResolveStorageProvider_NonAzureLane_DefaultsToS3() =>
        Assert.AreEqual(
            StorageProvider.S3,
            RegisterServices.ResolveStorageProvider(Config((HostingLaneResolver.LaneConfigurationKey, "NonAzure"))));

    [TestMethod]
    public void ResolveStorageProvider_CrossLaneValue_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RegisterServices.ResolveStorageProvider(Config(
                (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
                (RegisterServices.StorageProviderConfigKey, "AzureBlob"))));

    [TestMethod]
    public void ResolveStorageProvider_UnknownValue_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            RegisterServices.ResolveStorageProvider(Config((RegisterServices.StorageProviderConfigKey, "Ftp"))));

    [TestMethod]
    [DoNotParallelize]
    public void ResolveStorageProvider_EnvWinsOverConfig()
    {
        WithEnv(HostingLaneResolver.LaneEnvironmentVariable, "NonAzure", () =>
        WithEnv(RegisterServices.StorageProviderEnvVar, "S3", () =>
            Assert.AreEqual(
                StorageProvider.S3,
                RegisterServices.ResolveStorageProvider(Config((RegisterServices.StorageProviderConfigKey, "AzureBlob"))))));
    }

    [TestMethod]
    public void AddStorageServices_S3_RegistersS3ObjectStorageRepository()
    {
        var config = Config(
            (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
            (RegisterServices.StorageProviderConfigKey, "S3"),
            ("Storage:S3:ServiceUrl", "http://minio:9000"),
            ("Storage:S3:PublicServiceUrl", "http://localhost:9000"));

        var method = typeof(RegisterServices).GetMethod("AddStorageServices", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(RegisterServices), "AddStorageServices");
        var services = new ServiceCollection();
        services.AddLogging();

        method.Invoke(null, [services, config]);

        using var provider = services.BuildServiceProvider();
        Assert.IsInstanceOfType<S3ObjectStorageRepository>(provider.GetRequiredService<IObjectStorageRepository>());
    }

    [TestMethod]
    public async Task NoOpBlobStorageRepository_DeleteSucceeds_UploadDownloadAndUriThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IObjectStorageRepository, NoOpBlobStorageRepository>();
        var repo = services.BuildServiceProvider().GetRequiredService<IObjectStorageRepository>();
        var ct = TestContext.CancellationToken;

        // Delete is a real no-op success (already-gone semantics) so BlobDeleteWorkerService can complete leases.
        await repo.DeleteAsync("attachments", "missing", ct);
        Assert.IsFalse(await repo.ExistsAsync("attachments", "missing", ct));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repo.UploadAsync("attachments", "x", Stream.Null, cancellationToken: ct));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repo.DownloadAsync("attachments", "x", ct));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repo.GetPresignedUrlAsync("attachments", "x", TimeSpan.FromMinutes(5), cancellationToken: ct));
    }

    public TestContext TestContext { get; set; } = null!;

    // ----- ReadModel -----

    [TestMethod]
    public void ResolveReadModelProvider_Unset_AzureLane_DefaultsToCosmos() =>
        Assert.AreEqual(ReadModelProvider.Cosmos, RegisterServices.ResolveReadModelProvider(Config()));

    [TestMethod]
    public void ResolveReadModelProvider_NonAzureLane_DefaultsToPostgreSqlJsonb() =>
        Assert.AreEqual(
            ReadModelProvider.PostgreSqlJsonb,
            RegisterServices.ResolveReadModelProvider(Config((HostingLaneResolver.LaneConfigurationKey, "NonAzure"))));

    [TestMethod]
    public void ResolveReadModelProvider_CrossLaneValue_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RegisterServices.ResolveReadModelProvider(Config(
                (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
                (RegisterServices.ReadModelProviderConfigKey, "Cosmos"))));

    [TestMethod]
    public void ResolveReadModelProvider_UnknownValue_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            RegisterServices.ResolveReadModelProvider(Config((RegisterServices.ReadModelProviderConfigKey, "DocumentDb"))));

    [TestMethod]
    [DoNotParallelize]
    public void ResolveReadModelProvider_EnvWinsOverConfig()
    {
        WithEnv(HostingLaneResolver.LaneEnvironmentVariable, "NonAzure", () =>
        WithEnv(RegisterServices.ReadModelProviderEnvVar, "Relational", () =>
            Assert.AreEqual(
                ReadModelProvider.PostgreSqlJsonb,
                RegisterServices.ResolveReadModelProvider(Config((RegisterServices.ReadModelProviderConfigKey, "Cosmos"))))));
    }

    [TestMethod]
    public void AddReadModelServices_Relational_RegistersRelationalRepository()
    {
        var services = InvokeDispatcher("AddReadModelServices",
            Config(
                (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
                (RegisterServices.ReadModelProviderConfigKey, "Relational")));

        var descriptor = services.Single(d => d.ServiceType == typeof(ITaskViewRepository));
        Assert.AreEqual(typeof(RelationalTaskViewRepository), descriptor.ImplementationType);
        // Scoped, not singleton: it holds the two request-scoped DbContexts (D-027 write/read split).
        Assert.AreEqual(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    // ----- Audit -----

    [TestMethod]
    public void ResolveAuditProvider_Unset_AzureLane_DefaultsToAzureTable() =>
        Assert.AreEqual(AuditProvider.AzureTable, RegisterServices.ResolveAuditProvider(Config()));

    [TestMethod]
    public void ResolveAuditProvider_NonAzureLane_DefaultsToRelational() =>
        Assert.AreEqual(
            AuditProvider.Relational,
            RegisterServices.ResolveAuditProvider(Config((HostingLaneResolver.LaneConfigurationKey, "NonAzure"))));

    [TestMethod]
    public void ResolveAuditProvider_CrossLaneValue_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RegisterServices.ResolveAuditProvider(Config(
                (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
                (RegisterServices.AuditProviderConfigKey, "AzureTable"))));

    [TestMethod]
    public void ResolveAuditProvider_UnknownValue_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            RegisterServices.ResolveAuditProvider(Config((RegisterServices.AuditProviderConfigKey, "Mongo"))));

    [TestMethod]
    [DoNotParallelize]
    public void ResolveAuditProvider_EnvWinsOverConfig()
    {
        WithEnv(HostingLaneResolver.LaneEnvironmentVariable, "NonAzure", () =>
        WithEnv(RegisterServices.AuditProviderEnvVar, "Relational", () =>
            Assert.AreEqual(
                AuditProvider.Relational,
                RegisterServices.ResolveAuditProvider(Config((RegisterServices.AuditProviderConfigKey, "AzureTable"))))));
    }

    [TestMethod]
    public void AddAuditServices_Relational_RegistersRelationalSinkAndBindsSettings()
    {
        var services = InvokeDispatcher("AddAuditServices",
            Config(
                (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
                (RegisterServices.AuditProviderConfigKey, "Relational")));

        var descriptor = services.Single(d => d.ServiceType == typeof(IAuditLogRepository));
        Assert.AreEqual(ServiceLifetime.Scoped, descriptor.Lifetime);
        // The settings section carries the retention window the Scheduler job reads and the sentinel tenant
        // for entries with no tenant; leaving it unbound in this arm would silently use the defaults.
        Assert.IsTrue(
            services.Any(d => d.ServiceType == typeof(IConfigureOptions<AuditLogStorageSettings>)),
            "the relational arm must bind AuditLogStorageSettings as well");
    }

    // ----- Messaging (D-034 selector extended with the lane default and fail-fast in this slice) -----

    [TestMethod]
    public void ResolveMessagingProvider_Unset_AzureLane_DefaultsToServiceBus() =>
        Assert.AreEqual(MessagingProvider.ServiceBus, RegisterServices.ResolveMessagingProvider(Config()));

    [TestMethod]
    public void ResolveMessagingProvider_NonAzureLane_DefaultsToRabbitMq() =>
        Assert.AreEqual(
            MessagingProvider.RabbitMq,
            RegisterServices.ResolveMessagingProvider(Config((HostingLaneResolver.LaneConfigurationKey, "NonAzure"))));

    [TestMethod]
    public void ResolveMessagingProvider_CrossLaneValue_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RegisterServices.ResolveMessagingProvider(Config(
                (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
                (RegisterServices.MessagingProviderConfigKey, "ServiceBus"))));

    [TestMethod]
    public void ResolveMessagingProvider_UnknownValue_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            RegisterServices.ResolveMessagingProvider(Config((RegisterServices.MessagingProviderConfigKey, "Kafka"))));

    [TestMethod]
    [DoNotParallelize]
    public void ResolveMessagingProvider_EnvWinsOverConfig()
    {
        WithEnv(HostingLaneResolver.LaneEnvironmentVariable, "NonAzure", () =>
        WithEnv(RegisterServices.MessagingProviderEnvVar, "RabbitMq", () =>
            Assert.AreEqual(
                MessagingProvider.RabbitMq,
                RegisterServices.ResolveMessagingProvider(Config((RegisterServices.MessagingProviderConfigKey, "ServiceBus"))))));
    }

    // ----- DataProtection persistence -----

    [TestMethod]
    public void ResolveDataProtectionPersistence_Unset_AzureLane_DefaultsToAzureBlob() =>
        Assert.AreEqual(DataProtectionPersistence.AzureBlob, RegisterServices.ResolveDataProtectionPersistence(Config()));

    [TestMethod]
    public void ResolveDataProtectionPersistence_NonAzureLane_DefaultsToRedis() =>
        Assert.AreEqual(
            DataProtectionPersistence.Redis,
            RegisterServices.ResolveDataProtectionPersistence(Config((HostingLaneResolver.LaneConfigurationKey, "NonAzure"))));

    [TestMethod]
    public void ResolveDataProtectionPersistence_CrossLaneValue_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RegisterServices.ResolveDataProtectionPersistence(Config(
                (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
                (RegisterServices.DataProtectionPersistenceConfigKey, "AzureBlob"))));

    [TestMethod]
    public void ResolveDataProtectionPersistence_UnknownValue_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            RegisterServices.ResolveDataProtectionPersistence(Config((RegisterServices.DataProtectionPersistenceConfigKey, "FileShare"))));

    [TestMethod]
    [DoNotParallelize]
    public void ResolveDataProtectionPersistence_EnvWinsOverConfig()
    {
        WithEnv(RegisterServices.DataProtectionPersistenceEnvVar, "AzureBlob", () =>
            Assert.AreEqual(
                DataProtectionPersistence.AzureBlob,
                RegisterServices.ResolveDataProtectionPersistence(Config((RegisterServices.DataProtectionPersistenceConfigKey, "None")))));
    }

    /// <summary>
    /// Invokes a private static <c>Add&lt;X&gt;Services(IServiceCollection, IConfiguration)</c> dispatcher
    /// directly (bypassing the rest of <c>RegisterInfrastructureServices</c>, which needs a database
    /// connection and column-encryption key this test does not set up) and returns what it registered.
    /// </summary>
    private static IServiceCollection InvokeDispatcher(string methodName, IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Dispatcher(methodName).Invoke(null, [services, config]);
        return services;
    }

    /// <summary>Same invocation, for an arm that is expected to fail fast.</summary>
    private static NotSupportedException InvokeUnsupportedDispatcher(string methodName, IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var target = Assert.ThrowsExactly<TargetInvocationException>(() =>
            Dispatcher(methodName).Invoke(null, [services, config]));
        Assert.IsInstanceOfType<NotSupportedException>(target.InnerException);
        return (NotSupportedException)target.InnerException!;
    }

    private static MethodInfo Dispatcher(string methodName) =>
        typeof(RegisterServices).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(RegisterServices), methodName);

    /// <summary>Sets an environment variable for the duration of <paramref name="action"/>, always restoring it.</summary>
    private static void WithEnv(string name, string value, Action action)
    {
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
