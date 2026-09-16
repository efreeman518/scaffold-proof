using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace TaskFlow.Infrastructure.AI.Agents;

/// <summary>
/// Live task assistant backed by a Microsoft.Extensions.AI <see cref="IChatClient"/> (wired to Azure AI
/// Foundry or an OpenAI-compatible endpoint) and the Microsoft Agent Framework. It loads the embedded
/// system prompt, exposes TaskItemTools as function tools, and keeps one agent session per DI scope.
/// </summary>
public class TaskAssistantAgentService : ITaskAssistantAgent
{
    private readonly ChatClientAgent _agent;
    private readonly ILogger<TaskAssistantAgentService> _logger;

    // Per-conversation session tracking (scoped per user via DI)
    private AgentSession? _session;

    /// <summary>Initializes task assistant agent service with required dependencies and default state.</summary>
    public TaskAssistantAgentService(
        ILogger<TaskAssistantAgentService> logger,
        IChatClient chatClient,
        Tools.TaskItemTools tools)
    {
        _logger = logger;

        var systemPrompt = ReadEmbeddedPrompt("TaskAssistant.system-prompt.txt");

        // ChatClientAgent runs the agent loop (including function-tool invocation) over the injected
        // IChatClient, so the same agent works against any Foundry model the host wired.
        _agent = new ChatClientAgent(
            chatClient,
            instructions: systemPrompt,
            name: "TaskAssistant",
            description: "Assists with TaskFlow task management.",
            tools:
            [
                AIFunctionFactory.Create(
                    tools.SearchTasks,
                    "SearchTasks",
                    "Search for tasks by keyword, with optional status and priority filters"),
                AIFunctionFactory.Create(
                    tools.GetTaskDetails,
                    "GetTaskDetails",
                    "Get full details of a specific task by its ID"),
                AIFunctionFactory.Create(
                    tools.CreateTask,
                    "CreateTask",
                    "Create a new task with a title, optional description, and optional priority"),
                AIFunctionFactory.Create(
                    tools.UpdateTaskStatus,
                    "UpdateTaskStatus",
                    "Update the status of an existing task (Open, InProgress, Completed, Cancelled, Blocked)"),
                AIFunctionFactory.Create(
                    tools.SummarizeBacklog,
                    "SummarizeBacklog",
                    "Get a summary of all tasks grouped by status with overdue count")
            ]);
    }

    /// <summary>Provides the chat operation for task assistant agent service.</summary>
    public async Task<AgentChatResponse> ChatAsync(
        AgentChatRequest request, Guid? tenantId, CancellationToken ct = default)
    {
        _session ??= await _agent.CreateSessionAsync(ct);

        _logger.AssistantProcessing(tenantId, request.ConversationId);

        var response = await _agent.RunAsync(
            request.Message,
            _session,
            new ChatClientAgentRunOptions(new ChatOptions
            {
                ToolMode = request.UseTools ? ChatToolMode.Auto : ChatToolMode.None,
                Temperature = 0,
                MaxOutputTokens = 512
            }),
            cancellationToken: ct);

        return new AgentChatResponse
        {
            Message = response.ToString(),
            ConversationId = request.ConversationId ?? Guid.NewGuid().ToString(),
            IsConfigured = true
        };
    }

    /// <summary>Reads embedded prompt from the configured source.</summary>
    private static string ReadEmbeddedPrompt(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return string.Empty;

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
