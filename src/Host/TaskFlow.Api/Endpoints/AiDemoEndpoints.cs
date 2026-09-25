using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.AI.Demos;

namespace TaskFlow.Api.Endpoints;

/// <summary>
/// Maps the Azure AI Foundry inference demo routes. Each route demonstrates a distinct concept:
/// raw chat (D1), token streaming (D2), structured classification (D4), generative enrichment on
/// create (D5), and read-only multi-tool reasoning (D7). The conversational tool-calling agent (D3)
/// is mapped separately by <see cref="AgentEndpoints"/>.
/// </summary>
public static class AiDemoEndpoints
{
    private static readonly ChatOptions DemoChatOptions = new()
    {
        MaxOutputTokens = 128
    };

    /// <summary>Registers the AI demo routes under the /ai group.</summary>
    public static IEndpointRouteBuilder MapAiDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ai").WithTags("AI Demos");

        // D1 - Basic completion: prompt -> text via IChatClient.
        group.MapPost("/chat", async (
            [FromBody] AiChatRequest request,
            [FromServices] IChatClient chatClient,
            CancellationToken ct) =>
        {
            var response = await chatClient.GetResponseAsync(request.Message, DemoChatOptions, ct);
            return Results.Ok(new AiChatResponse(response.Text, chatClient is not NoOpChatClient));
        }).WithName("AiChat");

        // No-call status used by tests and diagnostics to distinguish live AI from no-op fallback.
        group.MapGet("/status", (
            [FromServices] IChatClient chatClient,
            [FromServices] AiProviderInfo provider) =>
            Results.Ok(new AiStatusResponse(provider.Name, chatClient is not NoOpChatClient)))
            .WithName("AiStatus");

        // D2 - Streaming completion: token stream as Server-Sent Events.
        group.MapPost("/chat/stream", (
            [FromBody] AiChatRequest request,
            [FromServices] IChatClient chatClient,
            CancellationToken ct) =>
            TypedResults.ServerSentEvents(StreamTokens(chatClient, request.Message, ct), eventType: "token"))
            .WithName("AiChatStream")
            // D-064: an open SSE stream has no fixed duration, so the host's default request timeout must
            // not cut it off.
            .DisableRequestTimeout();

        // D4 - Structured classification/decisioning: typed triage that can drive a deterministic apply.
        group.MapPost("/triage/{taskId:guid}", async (
            Guid taskId,
            [FromServices] ITaskTriageService triage,
            CancellationToken ct,
            bool apply = false) =>
            Results.Ok(await triage.TriageAsync(taskId, apply, ct)))
            .WithName("AiTriage");

        // D5 - Generative enrichment inside a write: draft description from a title, then create.
        group.MapPost("/tasks/draft", async (
            [FromBody] DraftTaskRequest request,
            [FromServices] ITaskDraftService draft,
            CancellationToken ct) =>
            Results.Ok(await draft.DraftAndCreateAsync(request.Title, ct)))
            .WithName("AiDraftTask");

        // D7 - Read-only multi-tool reasoning: recommend the next task, no side effects.
        group.MapPost("/next-action", async (
            [FromServices] INextActionAdvisor advisor,
            CancellationToken ct) =>
            Results.Ok(await advisor.RecommendAsync(ct)))
            .WithName("AiNextAction");

        return app;
    }

    /// <summary>Streams non-empty text chunks from the model as they arrive.</summary>
    private static async IAsyncEnumerable<string> StreamTokens(
        IChatClient chatClient, string message, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in chatClient.GetStreamingResponseAsync(message, DemoChatOptions, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }
}
