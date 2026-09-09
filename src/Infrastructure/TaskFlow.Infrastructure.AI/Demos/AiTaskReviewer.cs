using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;

namespace TaskFlow.Infrastructure.AI.Demos;

/// <inheritdoc />
public sealed class AiTaskReviewer(
    ILogger<AiTaskReviewer> logger,
    IChatClient chatClient,
    IVariantFeatureManager featureManager,
    ITaskItemService taskItemService) : IAiTaskReviewer
{
    private const string ReadyMarker = "READY";

    /// <inheritdoc />
    public async Task ReviewNewTaskAsync(Guid taskId, Guid tenantId, CancellationToken ct = default)
    {
        if (chatClient is NoOpChatClient)
        {
            logger.AiReviewerSkipped(taskId);
            return;
        }

        if (!await featureManager.IsEnabledAsync(TaskFlowFeatures.AiReview, ct))
        {
            logger.AiReviewerSkippedByFlag(taskId);
            return;
        }

        var getResult = await taskItemService.GetAsync(taskId, ct);
        if (getResult.IsNone || getResult.IsFailure)
        {
            logger.AiReviewerLoadFailed(taskId);
            return;
        }

        var task = getResult.Value!.Item!;

        var prompt = $$"""
            A new task was just created. Review it for readiness. If important details are missing or
            ambiguous, list 2-3 short clarifying questions a reviewer should resolve before work starts.
            If the task is already clear and actionable, reply with exactly: {{ReadyMarker}}
            Reply in plain text, no markdown.
            Title: {{task.Title}}
            Description: {{task.Description ?? "(none)"}}
            """;

        var response = await chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        var text = response.Text?.Trim() ?? string.Empty;

        if (text.Length == 0 || text.Equals(ReadyMarker, StringComparison.OrdinalIgnoreCase))
        {
            logger.AiReviewerReady(taskId);
            return;
        }

        var comment = new CommentDto
        {
            TenantId = tenantId,
            TaskItemId = taskId,
            Body = $"AI readiness review:\n{text}"
        };

        var result = await taskItemService.AddCommentAsync(taskId, comment, ct);
        if (result.IsFailure)
            logger.AiReviewerPostFailed(taskId, result.ErrorMessage);
        else
            logger.AiReviewerPosted(taskId);
    }
}
