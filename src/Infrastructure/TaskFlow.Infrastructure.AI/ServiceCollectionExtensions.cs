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

    private static SearchProvider ParseSearchProvider(string value) =>
        Enum.TryParse<SearchProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown search provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<SearchProvider>())}.");

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
                throw new NotSupportedException("Search provider PgVector is not implemented yet (slice P7).");

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
