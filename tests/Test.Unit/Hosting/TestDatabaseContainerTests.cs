using EF.IntegrationTesting.PostgreSql;

namespace Test.Unit.Hosting;

/// <summary>
/// Verifies isolated test database names always fit PostgreSQL's 63-byte identifier limit, which truncates
/// longer names silently instead of failing. The package fixtures behind <c>TestDatabaseContainer</c> validate
/// the prefix before touching the container, so no container is needed.
/// Unit tier: name validation only, no container.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TestDatabaseContainerTests
{
    private const int MaxDatabasePrefixLength = 30;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Given_APrefixAtTheLimit_When_NamingADatabase_Then_NameFitsPostgreSqlIdentifiers()
    {
        await using var fixture = new PostgreSqlContainerFixture();

        // Accepted by the name check; it then fails only because this fixture's container never started.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.CreateDatabaseAsync(new string('p', MaxDatabasePrefixLength), TestContext.CancellationToken));
    }

    /// <summary>A throwaway PostgreSQL database string is unpooled and otherwise unchanged (53300 "too many clients").</summary>
    [TestMethod]
    public void Given_AThrowawayPostgreSqlDatabase_When_ItsConnectionStringIsBuilt_Then_PoolingIsOff()
    {
        var unpooled = new Npgsql.NpgsqlConnectionStringBuilder(
            Test.Support.Hosting.TestDatabaseContainer.UnpooledPostgreSql("Host=localhost;Port=5432;Database=TaskFlow_x;Username=u;Password=p"));

        Assert.IsFalse(unpooled.Pooling);
        Assert.AreEqual("TaskFlow_x", unpooled.Database);
        Assert.AreEqual(5432, unpooled.Port);
    }

    [TestMethod]
    public async Task Given_APrefixOverTheLimit_When_NamingADatabase_Then_Throws()
    {
        await using var fixture = new PostgreSqlContainerFixture();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.CreateDatabaseAsync(new string('p', MaxDatabasePrefixLength + 1), TestContext.CancellationToken));
    }
}
