using EF.Audit.Contracts;
using EF.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Storage;
using TaskFlow.Infrastructure.Storage.CosmosDb;
using TaskFlow.Hosting;
using Test.Support;
using Test.Support.Hosting;

namespace Test.E2E;

/// <summary>
/// Real-database WebApplicationFactory backed by a Testcontainer for the provider selected by
/// <c>TASKFLOW_LANE</c> (Azure default, NonAzure alternate). Exercises the full stack:
/// HTTP -> endpoint style -> application layer -> EF -> database.
/// Set TASKFLOW_APPLICATION_STYLE=Cqrs to run the same workflow tests against CQRS endpoint mappings.
/// </summary>
public sealed class DbApiFactory : WebApplicationFactoryBase<Program, TaskFlowDbContextTrxn, TaskFlowDbContextQuery>
{
    private const string InertBlobEndpoint = "https://taskflow-e2e.blob.core.windows.net/";
    private const string InertTableEndpoint = "https://taskflow-e2e.table.core.windows.net/";
    private const string InertCosmosEndpoint = "https://taskflow-e2e.documents.azure.com:443/";
    private const string InertServiceBusNamespace = "taskflow-e2e.servicebus.windows.net";
    private const string InertS3Endpoint = "http://127.0.0.1:1";
    private const string InertRabbitMqConnection = "amqp://taskflow:taskflow@127.0.0.1:1/";

    private static readonly HostingLaneSettings Lane = TestHostingLane.Current;
    private static readonly TestDatabaseContainer Db = new(TestHostingLane.DatabaseProvider);
    private static readonly RedisTestContainer? Redis = Lane.IsNonAzure ? new RedisTestContainer() : null;

    // The container is shared by every test class in this assembly and cannot be restarted once
    // disposed, so it is started on first use and torn down once from [AssemblyCleanup]. Disposing it
    // from a class cleanup pulled it out from under the classes that ran afterwards.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _databaseStarted;

    private readonly string _applicationStyle;

    public static string? DockerUnavailableReason { get; private set; }

    public static Exception? StartupError { get; private set; }

    /// <summary>Initializes the API factory with required dependencies and default state.</summary>
    public DbApiFactory(string? applicationStyle = null)
    {
        _applicationStyle = applicationStyle
            ?? Environment.GetEnvironmentVariable(ApplicationStyleResolver.EnvironmentVariable)
            ?? ApplicationStyle.Service.ToString();
    }

    /// <summary>Starts the database container once per test run.</summary>
    public static async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_databaseStarted || DockerUnavailableReason is not null || StartupError is not null)
                return;

            DockerUnavailableReason = await DockerRuntimePreflight.GetUnavailableReasonAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
            if (DockerUnavailableReason is not null)
                return;

            try
            {
                await Db.StartAsync();
                _databaseStarted = true;
                if (Redis is not null)
                    await Redis.StartAsync();
            }
            catch (Exception ex)
            {
                StartupError = ex;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Stops the shared container. Called once per assembly, never from a class cleanup.</summary>
    public static async Task StopContainerAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!_databaseStarted && Redis?.IsStarted != true) return;

            try
            {
                if (Redis is not null)
                    await Redis.DisposeAsync();
            }
            finally
            {
                if (_databaseStarted)
                    await Db.DisposeAsync();
                _databaseStarted = false;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Strict lane providers are configured before Program registers them. Database operations remain on the
    /// real Testcontainer; unrelated external data planes are replaced after their configuration is validated.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in StrictLaneConfiguration())
            builder.UseSetting(key, value);

        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStorageRepository>();
            services.AddSingleton<IObjectStorageRepository, NoOpBlobStorageRepository>();
            services.RemoveAll<IIntegrationEventTransport>();
            services.AddSingleton<IIntegrationEventTransport, NoOpEventTransport>();

            if (Lane.Lane == HostingLane.Azure)
            {
                services.RemoveAll<IAuditLogRepository>();
                services.AddSingleton<IAuditLogRepository, NoOpAuditLogRepository>();
            }

            if (Lane.Lane == HostingLane.Azure || TestHostingLane.UsesMongoDb)
            {
                services.RemoveAll<ITaskViewRepository>();
                services.AddSingleton<ITaskViewRepository, NoOpTaskViewRepository>();
            }
        });
    }

    /// <summary>Points the host at the container and selects the lane's provider (Database:Provider).</summary>
    protected override void ConfigureTestConfiguration(IConfigurationBuilder config)
    {
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ApplicationStyleResolver.ConfigKey] = _applicationStyle,
            [TaskFlowDbProviderSelector.ConfigurationKey] = Db.Provider.ToString()
        });
        config.AddInMemoryCollection(StrictLaneConfiguration());
        config.AddInMemoryCollection(TestColumnEncryption.Configuration);
    }

    private static Dictionary<string, string?> StrictLaneConfiguration()
    {
        var settings = new Dictionary<string, string?>
        {
            [HostingLaneResolver.LaneConfigurationKey] = Lane.Lane.ToString()
        };

        if (Lane.Lane == HostingLane.Azure)
        {
            settings["DataProtectionKeysFileUrl"] = $"{InertBlobEndpoint}data-protection/keys.xml";
            settings["ConnectionStrings:BlobStorage1"] = InertBlobEndpoint;
            settings["ConnectionStrings:TableStorage1"] = InertTableEndpoint;
            settings["ConnectionStrings:CosmosDb1"] = InertCosmosEndpoint;
            settings["ServiceBus1:fullyQualifiedNamespace"] = InertServiceBusNamespace;
            return settings;
        }

        settings["ConnectionStrings:Redis1"] = Redis?.ConnectionString
            ?? throw new InvalidOperationException("The NonAzure E2E lane requires its Redis Testcontainer.");
        settings["Storage:S3:ServiceUrl"] = InertS3Endpoint;
        settings["Storage:S3:PublicServiceUrl"] = InertS3Endpoint;
        settings["Storage:S3:AccessKeyId"] = "taskflow-e2e";
        settings["Storage:S3:SecretAccessKey"] = "taskflow-e2e-secret";
        settings["Messaging:RabbitMq:ConnectionString"] = InertRabbitMqConnection;
        if (TestHostingLane.UsesMongoDb)
            settings["ConnectionStrings:MongoDb1"] = "mongodb://127.0.0.1:1";
        return settings;
    }

    /// <summary>Connection string both contexts are built against (package request 16 override point).</summary>
    protected override string ConnectionString => Db.ConnectionString;

    /// <summary>
    /// One override for both contexts instead of a BuildTrxnOptions/BuildQueryOptions pair: the package base
    /// routes both through here, and TaskFlow's two contexts share a migrations history table and schema.
    /// </summary>
    protected override DbContextOptions BuildOptionsFor<TContext>(string connectionString) =>
        Db.BuildOptions<TContext>(
            connectionString, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName);
}
