using EF.Cache;
using EF.Common.Contracts;
using EF.Data.Contracts;
using EF.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskFlow.Application.Contracts;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Caching;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;
using Test.Support;
using Test.Support.Builders;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Shared setup for the staged-race tests (D-073): the caller's request context, the real tenant boundary, an L1-only
/// cache and a seeded task, all against the lane's relational provider.
/// </summary>
internal static class RaceHarness
{
    internal static readonly Guid TenantGuid = TestConstants.TenantId;

    private static readonly ServiceProvider Services = BuildServices();

    internal static ITenantBoundaryValidator Boundary => Services.GetRequiredService<ITenantBoundaryValidator>();
    internal static ITypedCache Cache => Services.GetRequiredService<ITypedCache>();

    /// <summary>A cache of its own, so a test can observe an eviction no other test triggered.</summary>
    internal static ITypedCache NewCache() => BuildServices().GetRequiredService<ITypedCache>();

    internal static RequestContext<string, Guid?> RequestContext() =>
        new("race-test", "race-user", TenantGuid, [AppConstants.ROLE_TENANT_MEMBER]);

    /// <summary>Seeds one task for the race tenant and returns its id.</summary>
    internal static async Task<Guid> SeedTaskAsync(CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        var task = new TaskItemBuilder().WithTenantId(TenantGuid).WithTitle($"Race-{Guid.NewGuid():N}").Build();
        db.TaskItems.Add(task);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        return task.Id.Value;
    }

    /// <summary>The real tenant boundary and an L1-only cache.</summary>
    private static ServiceProvider BuildServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:0:Name"] = AppConstants.DEFAULT_CACHE,
                ["OpenTelemetry:MetricsEnabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new RaceTestHostEnvironment());
        services.AddTaskFlowCaching(config);
        services.AddTenancy(options => options.CrossTenantRoles = [AppConstants.ROLE_GLOBAL_ADMIN, AppConstants.ROLE_SYSTEM]);
        return services.BuildServiceProvider();
    }

    private sealed class RaceTestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTest";
        public string ApplicationName { get; set; } = nameof(RaceHarness);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

/// <summary>
/// Before the handler's first save (or before every save, to exhaust the retry), commits a competing write from
/// another context. The default write is a comment added through the root, so the root version moves past the one
/// the handler loaded. Counts the handler's saves.
/// </summary>
internal sealed class CompetingWrite(
    string connectionString, Guid taskId, Func<TaskFlowDbContextTrxn, CancellationToken, Task>? write = null, bool everySave = false)
    : SaveChangesInterceptor
{
    public const string CompetingBody = "competing comment";

    public bool Ran { get; private set; }
    public int Saves { get; private set; }
    public long SeededVersion { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Saves++;
        if (!Ran || everySave)
        {
            await using var other = DbContainerFixture.CreateTrxnContext(connectionString);
            if (write is null)
            {
                var root = await new TaskItemRepositoryTrxn(other).GetTaskItemAsync(TaskItemId.From(taskId), inclChildren: false, cancellationToken)
                    ?? throw new InvalidOperationException("seeded task not found");
                if (!Ran) SeededVersion = root.Version;
                Assert.IsTrue(root.AddComment(CompetingBody).IsSuccess);
            }
            else
            {
                await write(other, cancellationToken);
            }

            Ran = true;
            await other.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: cancellationToken);
        }

        return result;
    }
}
