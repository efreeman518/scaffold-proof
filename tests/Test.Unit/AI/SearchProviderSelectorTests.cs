using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Moq;
using TaskFlow.Application.Contracts.Configuration;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.AI.Search;
using TaskFlow.Infrastructure.Data.Provider;

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

    /// <summary>
    /// D-040 fail-fast, prerequisite 1: PgVector needs PostgreSQL, because the TaskItemEmbedding table is
    /// mapped only on the Npgsql provider. Silently degrading to prefix search would report results labelled
    /// semantic that are not.
    /// </summary>
    [TestMethod]
    public void AddAiServices_PgVectorOnSqlServer_ThrowsNamingTheSqlServerVectorFutureArm()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        services.AddSingleton(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>());
        var config = Config(
            (AiServiceCollectionExtensions.SearchProviderConfigKey, "PgVector"),
            (TaskFlowDbProviderSelector.ConfigurationKey, "SqlServer"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => services.AddAiServices(config));
        StringAssert.Contains(ex.Message, "PostgreSql");
        StringAssert.Contains(ex.Message, "VECTOR");
    }

    /// <summary>
    /// D-040 fail-fast, prerequisite 2: without an embedding generator there is nothing to embed the query
    /// with, so the arm cannot answer a semantic request at all.
    /// </summary>
    [TestMethod]
    public void AddAiServices_PgVectorWithoutEmbeddingGenerator_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        var config = Config(
            (AiServiceCollectionExtensions.SearchProviderConfigKey, "PgVector"),
            (TaskFlowDbProviderSelector.ConfigurationKey, "PostgreSql"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => services.AddAiServices(config));
        StringAssert.Contains(ex.Message, "IEmbeddingGenerator");
    }

    /// <summary>
    /// The dimension is the deployed column type, not a runtime knob: a value that disagrees with the
    /// migration would leave the HNSW index unusable, so it is refused rather than applied.
    /// </summary>
    [TestMethod]
    public void AddAiServices_PgVectorWithMismatchedDimensions_ThrowsNamingTheMigration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        services.AddSingleton(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>());
        var config = Config(
            (AiServiceCollectionExtensions.SearchProviderConfigKey, "PgVector"),
            (TaskFlowDbProviderSelector.ConfigurationKey, "PostgreSql"),
            (AiServiceCollectionExtensions.PgVectorDimensionsConfigKey, "3072"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => services.AddAiServices(config));
        StringAssert.Contains(ex.Message, "migration");
    }

    /// <summary>Both prerequisites met: the contract resolves to the pgvector arm, not the prefix fallback.</summary>
    [TestMethod]
    public void AddAiServices_PgVectorWithPrerequisitesMet_RegistersPgVectorSearchService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        services.AddSingleton(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Mock.Of<ITaskEmbeddingRepository>());
        var config = Config(
            (AiServiceCollectionExtensions.SearchProviderConfigKey, "PgVector"),
            (TaskFlowDbProviderSelector.ConfigurationKey, "PostgreSql"));

        services.AddAiServices(config);

        using var provider = services.BuildServiceProvider();
        Assert.IsInstanceOfType<PgVectorSearchService>(provider.GetRequiredService<ITaskFlowSearchService>());
    }
}
