using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>Verifies runtime database registration selects the configured provider and pins the schema-owned migration history.</summary>
[TestClass]
public sealed class DatabaseRegistrationTests
{
    [TestMethod]
    [DataRow(TaskFlowDbProvider.SqlServer, "Server=localhost;Database=TaskFlowRegistration;User Id=sa;Password=NotARealPassword1!;TrustServerCertificate=true", "[taskflow].[__EFMigrationsHistory]")]
    [DataRow(TaskFlowDbProvider.PostgreSql, "Host=localhost;Database=TaskFlowRegistration;Username=postgres;Password=NotARealPassword1!", "taskflow.\"__EFMigrationsHistory\"")]
    public void RegisterInfrastructureServices_PinsPrimaryMigrationHistoryToTaskFlowSchema(
        TaskFlowDbProvider provider, string connectionString, string expectedHistoryTable)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TaskFlowDbContextTrxn"] = connectionString,
                ["ConnectionStrings:TaskFlowDbContextQuery"] = connectionString,
                [RegisterServices.AuditProviderConfigKey] = AuditProvider.Relational.ToString(),
                [TaskFlowDbProviderSelector.ConfigurationKey] = provider.ToString()
            })
            .AddInMemoryCollection(TestColumnEncryption.Configuration)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterInfrastructureServices(configuration);

        using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
        using var scope = serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TaskFlowDbContextTrxn>>();
        using var db = factory.CreateDbContext();
        var createScript = db.GetService<IHistoryRepository>().GetCreateScript();

        Assert.AreEqual(provider == TaskFlowDbProvider.SqlServer, db.Database.IsSqlServer());
        Assert.AreEqual(provider == TaskFlowDbProvider.PostgreSql, db.Database.IsNpgsql());
        StringAssert.Contains(createScript, expectedHistoryTable);
    }
}
