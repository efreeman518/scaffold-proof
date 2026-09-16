using Microsoft.Extensions.Logging;

namespace TaskFlow.Infrastructure.AI.Agents;

/// <summary>Provides no op task assistant agent behavior for the Infrastructure Agents layer.</summary>
public class NoOpTaskAssistantAgent(ILogger<NoOpTaskAssistantAgent> logger) : ITaskAssistantAgent
{
    /// <summary>Provides the chat operation for no op task assistant agent.</summary>
    public Task<AgentChatResponse> ChatAsync(
        AgentChatRequest request, Guid? tenantId, CancellationToken ct = default)
    {
        logger.AssistantNotConfigured();
        return Task.FromResult(new AgentChatResponse
        {
            Message = "AI agent is not configured. Wire an Azure or OpenAI-compatible chat model to continue.",
            ConversationId = request.ConversationId ?? Guid.NewGuid().ToString(),
            IsConfigured = false
        });
    }
}
