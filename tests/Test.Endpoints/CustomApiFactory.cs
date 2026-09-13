using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using EF.Audit.Contracts;
using EF.Storage.Contracts;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Interceptors;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Storage;
using TaskFlow.Infrastructure.Storage.CosmosDb;
using Test.Support;

namespace Test.Endpoints;

/// <summary>
/// In-memory WebApplicationFactory for endpoint contract tests.
///
/// Uses EF Core <c>InMemoryDatabase</c> per factory instance so each test class gets an isolated DB.
/// Set TASKFLOW_APPLICATION_STYLE=Cqrs to run the same endpoint tests against CQRS endpoint mappings.
/// </summary>
public sealed class CustomApiFactory : WebApplicationFactoryBase<Program, TaskFlowDbContextTrxn, TaskFlowDbContextQuery>
{
    private const string TestDataProtectionKeysFileUrl =
        "https://taskflowtest.blob.core.windows.net/data-protection/keys.xml";
    private const string TestBlobEndpoint = "https://taskflowtest.blob.core.windows.net/";
    private const string TestTableEndpoint = "https://taskflowtest.table.core.windows.net/";
    private const string TestCosmosEndpoint = "https://taskflowtest.documents.azure.com:443/";
    private const string TestServiceBusNamespace = "taskflowtest.servicebus.windows.net";
    private readonly string _applicationStyle;
    private readonly string _dbName = $"TestDb_{Guid.NewGuid()}";

    /// <summary>Initializes custom API factory with required dependencies and default state.</summary>
    public CustomApiFactory(string? applicationStyle = null)
    {
        _applicationStyle = applicationStyle
            ?? Environment.GetEnvironmentVariable(ApplicationStyleResolver.EnvironmentVariable)
            ?? ApplicationStyle.Service.ToString();
    }

    /// <summary>
    /// The application style must be visible while Program.cs registers services, not only after the
    /// host is built: ConfigureAppConfiguration sources land too late for that, so the style goes in as
    /// a host setting. Without this the endpoint map would select CQRS routes while the container still
    /// held only the Service-style registrations.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ApplicationStyleResolver.ConfigKey, _applicationStyle);
        builder.UseSetting("DataProtectionKeysFileUrl", TestDataProtectionKeysFileUrl);
        builder.UseSetting("ConnectionStrings:BlobStorage1", TestBlobEndpoint);
        builder.UseSetting("ConnectionStrings:TableStorage1", TestTableEndpoint);
        builder.UseSetting("ConnectionStrings:CosmosDb1", TestCosmosEndpoint);
        builder.UseSetting("ServiceBus1:fullyQualifiedNamespace", TestServiceBusNamespace);
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            // Strict Azure registration still requires every core endpoint above. Endpoint tests replace
            // those external data planes explicitly so requests stay deterministic and network-free.
            services.RemoveAll<IObjectStorageRepository>();
            services.AddSingleton<IObjectStorageRepository, NoOpBlobStorageRepository>();
            services.RemoveAll<IAuditLogRepository>();
            services.AddSingleton<IAuditLogRepository, NoOpAuditLogRepository>();
            services.RemoveAll<IIntegrationEventTransport>();
            services.AddSingleton<IIntegrationEventTransport, NoOpEventTransport>();
            services.RemoveAll<ITaskViewRepository>();
            services.AddSingleton<ITaskViewRepository, NoOpTaskViewRepository>();
        });
    }

    /// <summary>Verifies configure test configuration behavior and protects the expected test contract.</summary>
    protected override void ConfigureTestConfiguration(IConfigurationBuilder config)
    {
        AddFoundryLocalDisabled(config);
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ApplicationStyleResolver.ConfigKey] = _applicationStyle,
            ["DataProtectionKeysFileUrl"] = TestDataProtectionKeysFileUrl,
            ["ConnectionStrings:BlobStorage1"] = TestBlobEndpoint,
            ["ConnectionStrings:TableStorage1"] = TestTableEndpoint,
            ["ConnectionStrings:CosmosDb1"] = TestCosmosEndpoint,
            ["ServiceBus1:fullyQualifiedNamespace"] = TestServiceBusNamespace
        });
        config.AddInMemoryCollection(TestColumnEncryption.Configuration);
    }

    // The version/timestamp interceptor is part of the concurrency contract (D-021), not of the SQL
    // provider: without it here every entity would report Version 0 and the whole ETag surface would
    // pass the tests while being inert.
    /// <summary>Builds trxn options used by focused test cases.</summary>
    protected override DbContextOptions BuildTrxnOptions() =>
        new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()
            .UseInMemoryDatabase(_dbName)
            .AddInterceptors(new VersionTimestampInterceptor())
            .Options;

    /// <summary>Builds query options used by focused test cases.</summary>
    protected override DbContextOptions BuildQueryOptions() =>
        new DbContextOptionsBuilder<TaskFlowDbContextQuery>()
            .UseInMemoryDatabase(_dbName)
            .AddInterceptors(new VersionTimestampInterceptor())
            .Options;
}
