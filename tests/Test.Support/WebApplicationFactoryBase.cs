using EF.Data;
using EF.IntegrationTesting.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Test.Support;

/// <summary>
/// TaskFlow-specific adapter over the reusable EF.IntegrationTesting WebApplicationFactory base.
/// Keeps test factories stable while moving shared EF host-replacement plumbing into the package project.
/// </summary>
public abstract class WebApplicationFactoryBase<TProgram, TTrxnContext, TQueryContext>
    : EfWebApplicationFactoryBase<TProgram, TTrxnContext, TQueryContext>
    where TProgram : class
    where TTrxnContext : DbContextBase<string, Guid?>
    where TQueryContext : DbContextBase<string, Guid?>
{
    protected override string? StartupTaskServiceTypeFullName => "TaskFlow.Bootstrapper.IStartupTask";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        });
    }

    /// <summary>
    /// The harness contexts carry no tenant, and EF.Data's tenant query filter reads nothing for a tenant-less
    /// context unless it is marked all-tenants. Marking them keeps harness reads unfiltered, as a null tenant
    /// read before EF.Data 2.0; tenant isolation is proven by the container-backed tests, not here.
    /// </summary>
    protected override void ConfigureAdditionalTestServices(IServiceCollection services)
    {
        AllTenants<TTrxnContext>(services);
        AllTenants<TQueryContext>(services);
    }

    private static void AllTenants<TContext>(IServiceCollection services)
        where TContext : DbContextBase<string, Guid?>
    {
        var scoped = services.Last(d => d.ServiceType == typeof(TContext)).ImplementationFactory!;
        services.AddScoped(sp =>
        {
            var context = (TContext)scoped(sp);
            context.AllTenants = true;
            return context;
        });

        var factory = (IDbContextFactory<TContext>)services
            .Last(d => d.ServiceType == typeof(IDbContextFactory<TContext>)).ImplementationInstance!;
        services.AddSingleton<IDbContextFactory<TContext>>(new EfTestDbContextFactory<TContext>(() =>
        {
            var context = factory.CreateDbContext();
            context.AllTenants = true;
            return context;
        }));
    }
}
