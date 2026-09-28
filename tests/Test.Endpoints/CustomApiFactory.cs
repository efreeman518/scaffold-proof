using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using EF.IntegrationTesting.EntityFramework;
using TaskFlow.Application.Contracts;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Interceptors;
using Test.Support;
using Test.Support.Hosting;

namespace Test.Endpoints;

/// <summary>
/// In-memory WebApplicationFactory for endpoint contract tests.
///
/// Uses EF Core <c>InMemoryDatabase</c> per factory instance so each test class gets an isolated DB.
/// Set TASKFLOW_APPLICATION_STYLE=Cqrs to run the same endpoint tests against CQRS endpoint mappings.
/// </summary>
public sealed class CustomApiFactory : WebApplicationFactoryBase<Program, TaskFlowDbContextTrxn, TaskFlowDbContextQuery>
{
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
        // D-060: the endpoint contract runs on the default NonAzure lane, pinned here, with every data plane
        // replaced in process so requests stay deterministic, network-free, and container-free.
        foreach (var (key, value) in HostSettings())
            builder.UseSetting(key, value);
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(InertNonAzureLane.ReplaceDataPlanes);
    }

    /// <summary>Verifies configure test configuration behavior and protects the expected test contract.</summary>
    protected override void ConfigureTestConfiguration(IConfigurationBuilder config)
    {
        config.AddInMemoryCollection(HostSettings());
        config.AddInMemoryCollection(TestColumnEncryption.Configuration);
    }

    private Dictionary<string, string?> HostSettings() => new(InertNonAzureLane.Settings)
    {
        [ApplicationStyleResolver.ConfigKey] = _applicationStyle
    };

    // The version/timestamp interceptor is part of the concurrency contract (D-021), not of the SQL
    // provider: without it here every entity would report Version 0 and the whole ETag surface would
    // pass the tests while being inert.
    /// <summary>Builds trxn options used by focused test cases.</summary>
    protected override DbContextOptions BuildTrxnOptions() =>
        DbContextOptionsFactory.BuildInMemoryOptions<TaskFlowDbContextTrxn>(
            _dbName, builder => builder.AddInterceptors(new VersionTimestampInterceptor()));

    /// <summary>Builds query options used by focused test cases.</summary>
    protected override DbContextOptions BuildQueryOptions() =>
        DbContextOptionsFactory.BuildInMemoryOptions<TaskFlowDbContextQuery>(
            _dbName, builder => builder.AddInterceptors(new VersionTimestampInterceptor()));
}
