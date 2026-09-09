using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace TaskFlow.Infrastructure.AI;

/// <summary>
/// Scaffold-mode <see cref="IChatClient"/> used when no Foundry model is wired. It returns a clear
/// "not configured" message instead of throwing, so the AI demo endpoints and any IChatClient
/// consumers resolve and the app boots without Foundry Local or Azure AI Foundry.
/// </summary>
public sealed class NoOpChatClient(ILogger<NoOpChatClient> logger) : IChatClient
{
    private const string NotConfigured =
        "AI model is not configured. Install or allow Foundry Local, or wire an Azure AI Foundry " +
        "deployment in the AppHost to enable AI responses.";

    /// <summary>Returns the canned not-configured response for a non-streaming call.</summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        logger.NoOpChatClientInvoked();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, NotConfigured)));
    }

    /// <summary>Yields the canned not-configured response as a single streaming update.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logger.NoOpChatClientStreamingInvoked();
        yield return new ChatResponseUpdate(ChatRole.Assistant, NotConfigured);
        await Task.CompletedTask;
    }

    /// <summary>No underlying service to surface.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <summary>No resources to release.</summary>
    public void Dispose() { }
}
