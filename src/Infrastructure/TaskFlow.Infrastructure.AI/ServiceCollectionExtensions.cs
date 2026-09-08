using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Application.Contracts.Configuration;
using TaskFlow.Infrastructure.AI.Agents;
using TaskFlow.Infrastructure.AI.Agents.Tools;
using TaskFlow.Infrastructure.AI.Search;
using TaskFlow.Infrastructure.Data.Provider;

namespace TaskFlow.Infrastructure.AI;

/// <summary>Search backend selected for this deployment (D-040).</summary>
public enum SearchProvider
{
    /// <summary>Azure AI Search.</summary>
    AzureAiSearch,

    /// <summary>Postgres pgvector similarity search. Requires Database:Provider=PostgreSql. Not implemented yet (slice P7).</summary>
    PgVector,

    /// <summary>SQL prefix search fallback (<see cref="NoOpSearchService"/>).</summary>
    Sql
}

/// <summary>Provides AI service collection extensions behavior for the Infrastructure layer.</summary>
public static class AiServiceCollectionExtensions
{
    public const string SearchProviderConfigKey = "Search:Provider";
    public const string SearchProviderEnvVar = "TASKFLOW_SEARCH_PROVIDER";

    /// <summary>
    /// Resolves the search backend. The environment variable wins over configuration; when neither is
    /// set, the Portable lane defaults to Sql (D-035) and the Azure lane falls back to the legacy
    /// <c>AiServices:UseSearch</c>(+<c>SearchEndpoint</c>) compatibility mapping kept one release (D-040).
    /// </summary>
    public static SearchProvider ResolveSearchProvider(IConfiguration config, TaskFlowAiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);
        var value = Environment.GetEnvironmentVariable(SearchProviderEnvVar) ?? config[SearchProviderConfigKey];
        if (!string.IsNullOrWhiteSpace(value)) return ParseSearchProvider(value);

        if (HostingLaneSelector.Resolve(config) == HostingLane.Portable)
            return SearchProvider.Sql;

        return settings.UseSearch && !string.IsNullOrWhiteSpace(settings.SearchEndpoint)
            ? SearchProvider.AzureAiSearch
            : SearchProvider.Sql;
    }

    /// <summary>
    /// Same resolution binding <c>AiServices</c> from configuration first, for callers outside this assembly
    /// that need the answer before <c>AddAiServices</c> runs (queue topology, consumer registration).
    /// </summary>
    public static SearchProvider ResolveSearchProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ResolveSearchProvider(
            config,
            config.GetSection(TaskFlowAiSettings.ConfigSectionName).Get<TaskFlowAiSettings>() ?? new TaskFlowAiSettings());
    }

    private static SearchProvider ParseSearchProvider(string value) =>
        Enum.TryParse<SearchProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown search provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<SearchProvider>())}.");

    /// <summary>Configuration key for the vector dimension the deployed pgvector column was created with.</summary>
    public const string PgVectorDimensionsConfigKey = "Search:PgVector:Dimensions";

    /// <summary>Vector dimension the PostgreSQL migration creates the <c>Embedding</c> column with.</summary>
    public const int PgVectorDefaultDimensions = 1536;

    /// <summary>
    /// Wires the PgVector arm (D-040), failing at startup rather than degrading, because both prerequisites
    /// are deployment facts a running app cannot recover from:
    /// <list type="bullet">
    /// <item>the entity is mapped only on Npgsql, so a SqlServer deployment has no table to query;</item>
    /// <item>without an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> there is nothing to embed the
    /// query with, and a silent fall back to prefix search would report semantic results that are not.</item>
    /// </list>
    /// </summary>
    private static void AddPgVectorSearch(IServiceCollection services, IConfiguration config)
    {
        var dbProvider = TaskFlowDbProviderSelector.Resolve(config);
        if (dbProvider != TaskFlowDbProvider.PostgreSql)
            throw new InvalidOperationException(
                $"{SearchProviderConfigKey}=PgVector requires {TaskFlowDbProviderSelector.ConfigurationKey}=PostgreSql; "
                + $"this deployment resolved {dbProvider}. The TaskItemEmbedding table is mapped only on the Npgsql "
                + "provider. A SQL Server 2025 VECTOR arm is the intended future alternative and does not exist yet - "
                + $"until then use {SearchProviderConfigKey}=Sql or AzureAiSearch on SQL Server.");

        if (!services.Any(d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>)))
            throw new InvalidOperationException(
                $"{SearchProviderConfigKey}=PgVector requires an IEmbeddingGenerator<string, Embedding<float>>, and none "
                + "is registered. Select an AI provider that wires one (AiServices:Provider=OpenAICompatible), or "
                + "configure ConnectionStrings:embeddings for the AzureInference arm.");

        var dimensions = config.GetValue<int?>(PgVectorDimensionsConfigKey) ?? PgVectorDefaultDimensions;
        if (dimensions != PgVectorDefaultDimensions)
            throw new InvalidOperationException(
                $"{PgVectorDimensionsConfigKey}={dimensions} does not match the {PgVectorDefaultDimensions}-dimension "
                + "vector column the deployed PostgreSQL migration created. The dimension is part of the column type "
                + "and of the HNSW index, so changing it needs a new migration, not a configuration change.");

        // The prefix arm is a concrete dependency of the PgVector service, not a second ITaskFlowSearchService
        // registration: only one implementation may resolve for the contract.
        services.AddScoped<NoOpSearchService>();
        services.AddScoped<ITaskFlowSearchService, PgVectorSearchService>();
    }

    /// <summary>Registers AI services dependencies in the service container.</summary>
    public static IServiceCollection AddAiServices(this IServiceCollection services, IConfiguration config)
    {
        var aiSection = config.GetSection(TaskFlowAiSettings.ConfigSectionName);
        services.AddOptions<TaskFlowAiSettings>()
            .Bind(aiSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var settings = aiSection.Get<TaskFlowAiSettings>() ?? new TaskFlowAiSettings();

        switch (ResolveSearchProvider(config, settings))
        {
            case SearchProvider.AzureAiSearch:
                if (string.IsNullOrWhiteSpace(settings.SearchEndpoint))
                    throw new InvalidOperationException(
                        $"{SearchProviderConfigKey}=AzureAiSearch requires AiServices:SearchEndpoint.");

                services.AddSingleton(new SearchClient(
                    new Uri(settings.SearchEndpoint),
                    settings.SearchIndexName,
                    new DefaultAzureCredential()));

                services.AddScoped<ITaskFlowSearchService, TaskFlowSearchService>();
                break;

            case SearchProvider.PgVector:
                AddPgVectorSearch(services, config);
                break;

            case SearchProvider.Sql:
                services.AddScoped<ITaskFlowSearchService, NoOpSearchService>();
                break;
        }

        // Agent function tools (always registered - agents and tests both need them)
        services.AddScoped<TaskItemTools>();

        // A live IChatClient is registered at the host for Azure Foundry or Foundry Local. Its
        // presence - not raw config - gates live AI.
        var hasChatClient = services.Any(d => d.ServiceType == typeof(IChatClient));

        // Agent services - live agent follows the host-wired IChatClient. Without a model, keep the
        // no-op stub so default local runs still boot without Foundry or cloud credentials.
        if (hasChatClient)
        {
            services.AddScoped<ITaskAssistantAgent, TaskAssistantAgentService>();
        }
        else
        {
            services.AddScoped<ITaskAssistantAgent, NoOpTaskAssistantAgent>();
        }

        // IChatClient fallback - if the host wired no Foundry model, register a no-op so the AI demo
        // endpoints and any IChatClient consumers resolve and the app boots without a model.
        if (!hasChatClient)
        {
            services.AddSingleton<IChatClient, NoOpChatClient>();
        }

        services.TryAddSingleton(new AiProviderInfo("none"));

        // Inference demo services (each demonstrates a distinct concept; all resolve over the
        // registered IChatClient - real or no-op):
        services.AddScoped<Demos.ITaskTriageService, Demos.TaskTriageService>();       // D4: structured classification
        services.AddScoped<Demos.ITaskDraftService, Demos.TaskDraftService>();         // D5: generative enrichment on create
        services.AddScoped<Application.Contracts.Services.IAiTaskReviewer, Demos.AiTaskReviewer>();             // D6: async event-driven inference
        services.AddScoped<Demos.INextActionAdvisor, Demos.NextActionAdvisor>();       // D7: read-only multi-tool reasoning

        return services;
    }
}
