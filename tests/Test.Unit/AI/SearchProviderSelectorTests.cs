using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TaskFlow.Application.Contracts.Configuration;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Infrastructure.AI;

namespace Test.Unit.AI;

/// <summary>
/// Selector-table coverage for the D-040 search provider switch: its D-035 lane default and its legacy
/// <c>AiServices:UseSearch</c>(+<c>SearchEndpoint</c>) compatibility mapping, kept one release for the
/// case <c>Search:Provider</c> is unset. <c>AiServiceRegistrationTests</c> covers the same legacy mapping
/// through <c>AddAiServices</c> end to end; this file targets the resolver and the new arms directly.
/// Pure-unit tier (in-memory IConfiguration / ServiceCollection): no live Azure AI Search endpoint.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class SearchProviderSelectorTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    [TestMethod]
    public void ResolveSearchProvider_Unset_UseSearchFalse_DefaultsToSql() =>
        Assert.AreEqual(
            SearchProvider.Sql,
            AiServiceCollectionExtensions.ResolveSearchProvider(Config(), new TaskFlowAiSettings { UseSearch = false }));

    [TestMethod]
    public void ResolveSearchProvider_Unset_UseSearchTrueWithEndpoint_DefaultsToAzureAiSearch() =>
        Assert.AreEqual(
            SearchProvider.AzureAiSearch,
            AiServiceCollectionExtensions.ResolveSearchProvider(
                Config(),
                new TaskFlowAiSettings { UseSearch = true, SearchEndpoint = "https://example.search.windows.net" }));

    [TestMethod]
    public void ResolveSearchProvider_Unset_UseSearchTrueNoEndpoint_DefaultsToSql() =>
        Assert.AreEqual(
            SearchProvider.Sql,
            AiServiceCollectionExtensions.ResolveSearchProvider(
                Config(), new TaskFlowAiSettings { UseSearch = true, SearchEndpoint = "" }));

    [TestMethod]
    public void ResolveSearchProvider_PortableLane_DefaultsToSql_EvenWithLegacyUseSearchTrue() =>
        Assert.AreEqual(
            SearchProvider.Sql,
            AiServiceCollectionExtensions.ResolveSearchProvider(
                Config((HostingLaneSelector.ConfigurationKey, "Portable")),
                new TaskFlowAiSettings { UseSearch = true, SearchEndpoint = "https://example.search.windows.net" }));

    [TestMethod]
    public void ResolveSearchProvider_ConfigBeatsLaneDefault() =>
        Assert.AreEqual(
            SearchProvider.AzureAiSearch,
            AiServiceCollectionExtensions.ResolveSearchProvider(
                Config(
                    (HostingLaneSelector.ConfigurationKey, "Portable"),
                    (AiServiceCollectionExtensions.SearchProviderConfigKey, "AzureAiSearch")),
                new TaskFlowAiSettings()));

    [TestMethod]
    public void ResolveSearchProvider_UnknownValue_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            AiServiceCollectionExtensions.ResolveSearchProvider(
                Config((AiServiceCollectionExtensions.SearchProviderConfigKey, "Elasticsearch")),
                new TaskFlowAiSettings()));

    [TestMethod]
    [DoNotParallelize]
    public void ResolveSearchProvider_EnvWinsOverConfig()
    {
        var original = Environment.GetEnvironmentVariable(AiServiceCollectionExtensions.SearchProviderEnvVar);
        Environment.SetEnvironmentVariable(AiServiceCollectionExtensions.SearchProviderEnvVar, "Sql");
        try
        {
            Assert.AreEqual(
                SearchProvider.Sql,
                AiServiceCollectionExtensions.ResolveSearchProvider(
                    Config((AiServiceCollectionExtensions.SearchProviderConfigKey, "AzureAiSearch")),
                    new TaskFlowAiSettings()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AiServiceCollectionExtensions.SearchProviderEnvVar, original);
        }
    }

    [TestMethod]
    public void AddAiServices_SearchProviderAzureAiSearch_NoEndpoint_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        var config = Config((AiServiceCollectionExtensions.SearchProviderConfigKey, "AzureAiSearch"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => services.AddAiServices(config));
        StringAssert.Contains(ex.Message, "SearchEndpoint");
    }

    [TestMethod]
    public void AddAiServices_SearchProviderPgVector_ThrowsNotSupported()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        var config = Config((AiServiceCollectionExtensions.SearchProviderConfigKey, "PgVector"));

        var ex = Assert.ThrowsExactly<NotSupportedException>(() => services.AddAiServices(config));
        StringAssert.Contains(ex.Message, "P7");
    }
}
