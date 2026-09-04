using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Infrastructure.AI.Demos;

/// <summary>
/// D4 - Structured classification / decisioning. The model classifies an existing task and returns a
/// typed object that drives a deterministic decision (optionally applying the suggested priority).
/// </summary>
public interface ITaskTriageService
{
    /// <summary>Classifies the task and optionally applies the suggested priority.</summary>
    Task<TaskTriageResponse> TriageAsync(Guid taskId, bool apply, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TaskTriageService(
    ILogger<TaskTriageService> logger,
    IChatClient chatClient,
    ITaskItemService taskItemService) : ITaskTriageService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly ChatOptions TriageOptions = new()
    {
        Temperature = 0,
        MaxOutputTokens = 128
    };

    /// <inheritdoc />
    public async Task<TaskTriageResponse> TriageAsync(Guid taskId, bool apply, CancellationToken ct = default)
    {
        var getResult = await taskItemService.GetAsync(taskId, ct);
        if (getResult.IsNone)
            return new TaskTriageResponse(taskId, null, false, true, $"Task {taskId} not found.");
        if (getResult.IsFailure)
            return new TaskTriageResponse(taskId, null, false, true, getResult.ErrorMessage);

        var task = getResult.Value!.Item!;

        if (chatClient is NoOpChatClient)
            return new TaskTriageResponse(taskId, null, false, false, "AI model not configured.");

        var prompt = $$"""
            You are triaging a task management item. Respond with ONLY minified JSON, no markdown, matching:
            {"suggestedPriority":"None|Low|Medium|High|Critical","suggestedCategory":"string","confidence":0.0,"rationale":"short string"}
            Title: {{task.Title}}
            Description: {{task.Description ?? "(none)"}}
            """;

        var response = await chatClient.GetResponseAsync(prompt, TriageOptions, cancellationToken: ct);
        var triage = ParseTriage(response.Text);
        if (triage is null || !IsValidTriage(triage))
        {
            logger.LogWarning("Triage for {TaskId} returned unparseable output.", taskId);
            return new TaskTriageResponse(taskId, null, false, true, "Could not parse model output as triage JSON.");
        }

        var applied = false;
        if (apply && Enum.TryParse<Priority>(triage.SuggestedPriority, ignoreCase: true, out var priority))
        {
            task.Priority = priority;
            var update = await taskItemService.UpdateAsync(new DefaultRequest<TaskItemDto> { Item = task }, task.Version, ct);
            applied = !update.IsFailure;
            if (!applied)
                logger.LogWarning("Failed to apply triage priority to {TaskId}: {Error}", taskId, update.ErrorMessage);
        }

        return new TaskTriageResponse(taskId, triage, applied, true);
    }

    /// <summary>Tolerantly parses the model's JSON, stripping any markdown code fences.</summary>
    private static TaskTriageResult? ParseTriage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var json = ExtractJsonObject(text);
        if (json is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<TaskTriageResult>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValidTriage(TaskTriageResult triage) =>
        !string.IsNullOrWhiteSpace(triage.SuggestedPriority)
        && Enum.TryParse<Priority>(triage.SuggestedPriority, ignoreCase: true, out _)
        && triage.Confidence is >= 0 and <= 1;

    /// <summary>Returns the substring from the first '{' to the last '}', or null if none.</summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
