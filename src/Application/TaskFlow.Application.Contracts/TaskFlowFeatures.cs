namespace TaskFlow.Application.Contracts;

/// <summary>
/// Dynamic feature-flag names (D-042), resolved via Microsoft.FeatureManagement. Backed by Azure App
/// Configuration when <c>AppConfig:Endpoint</c> is set, else the <c>FeatureManagement</c> section in
/// appsettings (every flag on locally). Referenced from both the Api (endpoint filters) and
/// Infrastructure.AI (consumer-side checks), so the names live here rather than in either project.
/// </summary>
public static class TaskFlowFeatures
{
    /// <summary>Gates the TaskView read-model HTTP surface; disabled answers 404 (endpoint filter).</summary>
    public const string TaskViews = "TaskViews";

    /// <summary>Gates the NDJSON task export HTTP surface; disabled answers 404 (endpoint filter).</summary>
    public const string Export = "Export";

    /// <summary>Gates the PgVector semantic search HTTP surface; disabled answers 404, see GR-19 (endpoint filter, applied by P7).</summary>
    public const string SemanticSearch = "SemanticSearch";

    /// <summary>Gates AiTaskReviewer's AI-assisted review consumer path via IVariantFeatureManager.</summary>
    public const string AiReview = "AiReview";
}
