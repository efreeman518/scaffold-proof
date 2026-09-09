using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TaskFlow.Infrastructure.AI.Agents.Tools;

namespace TaskFlow.Infrastructure.AI.Demos;

/// <summary>
/// D7 - Read-only multi-tool reasoning. A code-hosted agent composes several read-only TaskItemTools to
/// reason over the backlog and recommend what to work on next. No persistence and no mutating tools, so
/// it contrasts with the conversational agent (D3) and the workflow demos.
/// </summary>
public interface INextActionAdvisor
{
    /// <summary>Recommends the most important next task and explains why.</summary>
    Task<NextActionResponse> RecommendAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class NextActionAdvisor(
    ILogger<NextActionAdvisor> logger,
    IChatClient chatClient,
    TaskItemTools tools) : INextActionAdvisor
{
    private const string Instructions =
        "You are a planning assistant. Use the tools to inspect the backlog, then recommend the single " +
        "most important task to work on next and explain why in 2-3 sentences. You are read-only: never " +
        "create or modify tasks.";

    /// <inheritdoc />
    public async Task<NextActionResponse> RecommendAsync(CancellationToken ct = default)
    {
        if (chatClient is NoOpChatClient)
            return new NextActionResponse("AI model not configured.", false);

        // Only read-only tools are exposed - the advisor cannot mutate state.
        var agent = new ChatClientAgent(
            chatClient,
            instructions: Instructions,
            name: "NextActionAdvisor",
            description: "Read-only backlog planner.",
            tools:
            [
                AIFunctionFactory.Create(
                    tools.SummarizeBacklog, "SummarizeBacklog",
                    "Get a summary of all tasks grouped by status with overdue count"),
                AIFunctionFactory.Create(
                    tools.SearchTasks, "SearchTasks",
                    "Search for tasks by keyword, with optional status and priority filters"),
                AIFunctionFactory.Create(
                    tools.GetTaskDetails, "GetTaskDetails",
                    "Get full details of a specific task by its ID")
            ]);

        var session = await agent.CreateSessionAsync(ct);
        var response = await agent.RunAsync("What should I work on next, and why?", session, cancellationToken: ct);

        logger.NextActionAdvisorProduced();
        return new NextActionResponse(response.ToString(), true);
    }
}
