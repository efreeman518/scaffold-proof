using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;

namespace TaskFlow.Infrastructure.AI.Demos;

/// <summary>
/// D5 - Generative inference embedded inside an application write. From a terse title the model drafts
/// a fuller description and acceptance criteria, then the task is created through the normal service so
/// validation, tenant rules, audit, and events all run. Inference is one step inside the create workflow.
/// </summary>
public interface ITaskDraftService
{
    /// <summary>Drafts description + acceptance criteria from a title, then creates the task.</summary>
    Task<DraftTaskResponse> DraftAndCreateAsync(string title, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TaskDraftService(
    ILogger<TaskDraftService> logger,
    IChatClient chatClient,
    ITaskItemService taskItemService) : ITaskDraftService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Draft(string? Description, string? AcceptanceCriteria);

    /// <inheritdoc />
    public async Task<DraftTaskResponse> DraftAndCreateAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new DraftTaskResponse(null, title, null, null, false, true, "Title is required.");

        if (chatClient is NoOpChatClient)
            return new DraftTaskResponse(null, title, null, null, false, false, "AI model not configured.");

        var prompt = $$"""
            A user is creating a task with only a short title. Draft a clear, concise description and
            acceptance criteria. Respond with ONLY minified JSON, no markdown, matching:
            {"description":"2-3 sentence description","acceptanceCriteria":"bullet-style, newline separated"}
            Title: {{title}}
            """;

        var response = await chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        var draft = ParseDraft(response.Text);
        if (draft is null)
        {
            logger.TaskDraftUnparseable(title);
            return new DraftTaskResponse(null, title, null, null, false, true, "Could not parse model output.");
        }

        var description = draft.AcceptanceCriteria is { Length: > 0 }
            ? $"{draft.Description}\n\nAcceptance criteria:\n{draft.AcceptanceCriteria}"
            : draft.Description;

        var dto = new TaskItemDto { Title = title, Description = description };
        var create = await taskItemService.CreateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, ct);
        if (create.IsFailure)
            return new DraftTaskResponse(null, title, draft.Description, draft.AcceptanceCriteria, false, true, create.ErrorMessage);

        return new DraftTaskResponse(
            create.Value!.Item!.Id, title, draft.Description, draft.AcceptanceCriteria, true, true);
    }

    private static Draft? ParseDraft(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            return JsonSerializer.Deserialize<Draft>(text[start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
