using Test.Support.Hosting;

namespace Test.Unit.Hosting;

/// <summary>
/// Verifies isolated test database names always fit PostgreSQL's 63-byte identifier limit, which truncates
/// longer names silently instead of failing.
/// Unit tier: name generation only, no container.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TestDatabaseContainerTests
{
    [TestMethod]
    public void Given_APrefixAtTheLimit_When_NamingADatabase_Then_NameFitsPostgreSqlIdentifiers()
    {
        var name = TestDatabaseContainer.NewDatabaseName(new string('p', TestDatabaseContainer.MaxDatabasePrefixLength));

        Assert.IsLessThanOrEqualTo(63, name.Length);
    }

    [TestMethod]
    public void Given_APrefixOverTheLimit_When_NamingADatabase_Then_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            TestDatabaseContainer.NewDatabaseName(new string('p', TestDatabaseContainer.MaxDatabasePrefixLength + 1)));
}
