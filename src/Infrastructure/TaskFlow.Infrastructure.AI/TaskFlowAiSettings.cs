namespace TaskFlow.Infrastructure.AI;

/// <summary>Provides task flow AI behavior for the Infrastructure layer.</summary>
public class TaskFlowAiSettings
{
    public const string ConfigSectionName = "AiServices";

    // Legacy feature behavior only. D-060 resolves Search:Provider independently and defaults both lanes to Sql.
    public bool UseSearch { get; set; }
    public bool UseAgents { get; set; }
    public bool UseVectorSearch { get; set; }

    // TODO: [CONFIGURE] Set to your Azure AI Foundry endpoint
    public string? FoundryEndpoint { get; set; }

    // TODO: [CONFIGURE] Model deployment names in your Foundry project
    public string AgentModelDeployment { get; set; } = "gpt-4o-deploy";
    public string EmbeddingModelDeployment { get; set; } = "embedding-deploy";

    // TODO: [CONFIGURE] Set to your Azure AI Search endpoint
    public string? SearchEndpoint { get; set; }
    public string SearchIndexName { get; set; } = "taskitems-index";

    // OPT-IN (Azure-only): consume a Foundry project / server-hosted (prompt or pre-existing) agent.
    // FoundryProjectEndpoint is the project URI (or the Aspire-injected PROJ_URI); FoundryAgentName is
    // the id/name of a pre-existing agent created in the portal/IaC. Both empty -> the app uses the
    // code-hosted ChatClientAgent over the injected IChatClient. See TaskFlow.Api Program.cs.
    // TODO: [CONFIGURE] Foundry project endpoint + pre-existing agent name
    public string? FoundryProjectEndpoint { get; set; }
    public string? FoundryAgentName { get; set; }
}
