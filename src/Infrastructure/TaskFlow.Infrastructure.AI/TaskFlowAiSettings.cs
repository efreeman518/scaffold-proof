namespace TaskFlow.Infrastructure.AI;

/// <summary>Provides task flow AI behavior for the Infrastructure layer.</summary>
public class TaskFlowAiSettings
{
    public const string ConfigSectionName = "AiServices";

    // COMPATIBILITY (one release, D-040): read only when Search:Provider/TASKFLOW_SEARCH_PROVIDER is
    // unset, to derive the Azure-lane default (UseSearch + SearchEndpoint present -> AzureAiSearch, else
    // Sql). Prefer Search:Provider for new configuration.
    public bool UseSearch { get; set; }
    public bool UseAgents { get; set; }
    public bool UseVectorSearch { get; set; }

    // TODO: [CONFIGURE] Set to your Azure AI Foundry endpoint
    public string? FoundryEndpoint { get; set; }

    // Foundry Local SDK-direct fallback used by TaskFlow.Api when Azure is absent and not disabled.
    public string LocalModel { get; set; } = "qwen2.5-0.5b";
    public string LocalWebUrl { get; set; } = "http://127.0.0.1:52415";

    // TODO: [CONFIGURE] Fast/RID-free tests set this true so native Foundry Local never starts there.
    public bool DisableFoundryLocal { get; set; }

    // TODO: [CONFIGURE] Model deployment names in your Foundry project
    public string AgentModelDeployment { get; set; } = "gpt-4o-deploy";
    public string EmbeddingModelDeployment { get; set; } = "embedding-deploy";

    // TODO: [CONFIGURE] Set to your Azure AI Search endpoint
    public string? SearchEndpoint { get; set; }
    public string SearchIndexName { get; set; } = "taskitems-index";

    // OPT-IN: connect to an EXISTING Azure AI Foundry account instead of provisioning a new one.
    // These feed the AppHost RunAsExisting/PublishAsExisting parameters (commented in AppHost.cs).
    // TODO: [CONFIGURE] Existing Foundry account name + resource group
    public string? FoundryResourceName { get; set; }
    public string? FoundryResourceGroup { get; set; }

    // OPT-IN (Azure-only): consume a Foundry project / server-hosted (prompt or pre-existing) agent.
    // FoundryProjectEndpoint is the project URI (or the Aspire-injected PROJ_URI); FoundryAgentName is
    // the id/name of a pre-existing agent created in the portal/IaC. Both empty -> the app uses the
    // code-hosted ChatClientAgent over the injected IChatClient. See TaskFlow.Api Program.cs.
    // TODO: [CONFIGURE] Foundry project endpoint + pre-existing agent name
    public string? FoundryProjectEndpoint { get; set; }
    public string? FoundryAgentName { get; set; }
}
