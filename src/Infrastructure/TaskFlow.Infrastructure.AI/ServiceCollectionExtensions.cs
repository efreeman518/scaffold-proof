using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Infrastructure.AI.Agents;
using TaskFlow.Infrastructure.AI.Agents.Tools;
using TaskFlow.Infrastructure.AI.Search;

namespace TaskFlow.Infrastructure.AI;

/// <summary>Provides AI service collection extensions behavior for the Infrastructure layer.</summary>
public static class AiServiceCollectionExtensions
{
    /// <summary>Registers AI services dependencies in the service container.</summary>
    public static IServiceCollection AddAiServices(this IServiceCollection services, IConfiguration config)
    {
        var aiSection = config.GetSection(TaskFlowAiSettings.ConfigSectionName);
        services.AddOptions<TaskFlowAiSettings>()
            .Bind(aiSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var settings = aiSection.Get<TaskFlowAiSettings>() ?? new TaskFlowAiSettings();

        // Azure AI Search (if configured)
        if (settings.UseSearch)
        {
            if (!string.IsNullOrWhiteSpace(settings.SearchEndpoint))
            {
                services.AddSingleton(new SearchClient(
                    new Uri(settings.SearchEndpoint),
                    settings.SearchIndexName,
                    new DefaultAzureCredential()));

                services.AddScoped<ITaskFlowSearchService, TaskFlowSearchService>();
            }
            else
            {
                // TODO: [CONFIGURE] AI Search endpoint - set AiServices:SearchEndpoint for live search
                services.AddScoped<ITaskFlowSearchService, NoOpSearchService>();
            }
        }
        else
        {
            services.AddScoped<ITaskFlowSearchService, NoOpSearchService>();
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
