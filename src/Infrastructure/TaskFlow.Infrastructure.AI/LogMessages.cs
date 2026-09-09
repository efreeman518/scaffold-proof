using Microsoft.Extensions.Logging;
using TaskFlow.Infrastructure.AI.Search;
using TaskFlow.Observability;

namespace TaskFlow.Infrastructure.AI;

/// <summary>
/// Source-generated logging methods for the Infrastructure.AI layer. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs an agent SearchTasks tool invocation.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 1, Level = LogLevel.Debug, Message = "Agent tool: SearchTasks query='{Query}' status={Status} priority={Priority}")]
    public static partial void AgentSearchTasks(this ILogger logger, string query, string status, string priority);

    /// <summary>Logs an agent GetTaskDetails tool invocation.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 2, Level = LogLevel.Debug, Message = "Agent tool: GetTaskDetails id={TaskId}")]
    public static partial void AgentGetTaskDetails(this ILogger logger, string taskId);

    /// <summary>Logs an agent CreateTask tool invocation.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 3, Level = LogLevel.Debug, Message = "Agent tool: CreateTask title='{Title}'")]
    public static partial void AgentCreateTask(this ILogger logger, string title);

    /// <summary>Logs an agent UpdateTaskStatus tool invocation.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 4, Level = LogLevel.Debug, Message = "Agent tool: UpdateTaskStatus id={TaskId} status={Status}")]
    public static partial void AgentUpdateTaskStatus(this ILogger logger, string taskId, string status);

    /// <summary>Logs that the AI task reviewer was skipped because no model is configured.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 5, Level = LogLevel.Debug, Message = "AiTaskReviewer skipped for {TaskId} - no model configured.")]
    public static partial void AiReviewerSkipped(this ILogger logger, Guid taskId);

    /// <summary>Logs that the AI task reviewer could not load the task.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 6, Level = LogLevel.Debug, Message = "AiTaskReviewer could not load {TaskId}.")]
    public static partial void AiReviewerLoadFailed(this ILogger logger, Guid taskId);

    /// <summary>Logs that the AI task reviewer found the task ready and posted no comment.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 7, Level = LogLevel.Debug, Message = "AiTaskReviewer found {TaskId} ready; no comment posted.")]
    public static partial void AiReviewerReady(this ILogger logger, Guid taskId);

    /// <summary>Logs that the AI task reviewer posted a readiness comment.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 8, Level = LogLevel.Information, Message = "AiTaskReviewer posted a readiness comment on {TaskId}.")]
    public static partial void AiReviewerPosted(this ILogger logger, Guid taskId);

    /// <summary>Logs the number of results returned from AI Search.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 9, Level = LogLevel.Debug, Message = "AI Search returned {Count} results for query '{Query}' (mode={Mode})")]
    public static partial void SearchReturned(this ILogger logger, int count, string query, SearchMode mode);

    /// <summary>Logs that a task item was indexed in AI Search.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 10, Level = LogLevel.Debug, Message = "Indexed task item '{Id}' in search")]
    public static partial void SearchIndexed(this ILogger logger, string id);

    /// <summary>Logs that a task item was removed from the AI Search index.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 11, Level = LogLevel.Debug, Message = "Removed task item '{Id}' from search index")]
    public static partial void SearchRemoved(this ILogger logger, string id);

    /// <summary>Logs that AI Search is not configured and the query fell back to the SQL prefix search.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 15, Level = LogLevel.Debug, Message = "AI Search not configured - answering '{Query}' ({Mode}) with the SQL title-prefix search")]
    public static partial void SearchNotConfiguredPrefixFallback(this ILogger logger, string query, string mode);

    /// <summary>Logs that AI Search is not configured and indexing is skipped.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 12, Level = LogLevel.Debug, Message = "AI Search not configured - skipping index for document '{Id}'")]
    public static partial void SearchNotConfiguredIndexSkipped(this ILogger logger, string id);

    /// <summary>Logs that AI Search is not configured and removal is skipped.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 13, Level = LogLevel.Debug, Message = "AI Search not configured - skipping removal for '{Id}'")]
    public static partial void SearchNotConfiguredRemovalSkipped(this ILogger logger, string id);

    /// <summary>Logs that the task assistant is processing a message.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 14, Level = LogLevel.Debug, Message = "TaskAssistant processing message for tenant {TenantId}, conversation {ConversationId}")]
    public static partial void AssistantProcessing(this ILogger logger, Guid? tenantId, string? conversationId);

    /// <summary>Logs that the AI task reviewer was skipped because the AiReview feature flag is off (D-042).</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 16, Level = LogLevel.Debug, Message = "AiTaskReviewer skipped for {TaskId} - AiReview feature flag is off.")]
    public static partial void AiReviewerSkippedByFlag(this ILogger logger, Guid taskId);

    /// <summary>Logs an agent SummarizeBacklog tool invocation.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 17, Level = LogLevel.Debug, Message = "Agent tool: SummarizeBacklog")]
    public static partial void AgentSummarizeBacklog(this ILogger logger);

    /// <summary>Logs that the task assistant agent is not configured and returned a stub response.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 18, Level = LogLevel.Warning, Message = "TaskAssistant agent not configured - returning stub response")]
    public static partial void AssistantNotConfigured(this ILogger logger);

    /// <summary>Logs a non-streaming call to the no-op chat client.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 19, Level = LogLevel.Warning, Message = "NoOpChatClient invoked - no Foundry model configured.")]
    public static partial void NoOpChatClientInvoked(this ILogger logger);

    /// <summary>Logs a streaming call to the no-op chat client.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 20, Level = LogLevel.Warning, Message = "NoOpChatClient streaming invoked - no Foundry model configured.")]
    public static partial void NoOpChatClientStreamingInvoked(this ILogger logger);

    /// <summary>Logs that the AI task reviewer's readiness comment failed to post.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 21, Level = LogLevel.Warning, Message = "AiTaskReviewer failed to post comment on {TaskId}: {Error}")]
    public static partial void AiReviewerPostFailed(this ILogger logger, Guid taskId, string? error);

    /// <summary>Logs that the next-action advisor produced a recommendation.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 22, Level = LogLevel.Debug, Message = "NextActionAdvisor produced a recommendation.")]
    public static partial void NextActionAdvisorProduced(this ILogger logger);

    /// <summary>Logs that a task draft's model output could not be parsed.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 23, Level = LogLevel.Warning, Message = "Task draft for '{Title}' returned unparseable output.")]
    public static partial void TaskDraftUnparseable(this ILogger logger, string title);

    /// <summary>Logs that a triage's model output could not be parsed.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 24, Level = LogLevel.Warning, Message = "Triage for {TaskId} returned unparseable output.")]
    public static partial void TaskTriageUnparseable(this ILogger logger, Guid taskId);

    /// <summary>Logs that applying a suggested triage priority failed.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureAiBase + 25, Level = LogLevel.Warning, Message = "Failed to apply triage priority to {TaskId}: {Error}")]
    public static partial void TaskTriageApplyFailed(this ILogger logger, Guid taskId, string? error);
}
