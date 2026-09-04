using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Data;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>Verifies runtime database registration preserves schema-owned migration history.</summary>
[TestClass]
public sealed class DatabaseRegistrationTests
{
    [TestMethod]
    public void RegisterInfrastructureServices_PinsPrimaryMigrationHistoryToTaskFlowSchema()
    {
        const string connectionString =
            "Server=localhost;Database=TaskFlowRegistration;User Id=sa;Password=NotARealPassword1!;TrustServerCertificate=true";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TaskFlowDbContextTrxn"] = connectionString,
                ["ConnectionStrings:TaskFlowDbContextQuery"] = connectionString
            })
            .AddInMemoryCollection(TestColumnEncryption.Configuration)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterInfrastructureServices(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TaskFlowDbContextTrxn>>();
        using var db = factory.CreateDbContext();
        var createScript = db.GetService<IHistoryRepository>().GetCreateScript();

        StringAssert.Contains(
            createScript,
            $"[{TaskFlowDbContextBase.SchemaName}].[{TaskFlowDbContextBase.MigrationHistoryTable}]");
    }
}
