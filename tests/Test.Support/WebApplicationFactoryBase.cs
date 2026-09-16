using EF.Data;
using EF.IntegrationTesting.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
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

}
