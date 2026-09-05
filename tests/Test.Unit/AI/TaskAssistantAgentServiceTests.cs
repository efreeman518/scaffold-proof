using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Runtime.CompilerServices;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Infrastructure.AI.Agents;
using TaskFlow.Infrastructure.AI.Agents.Tools;
using TaskFlow.Infrastructure.AI.Search;

namespace Test.Unit.AI;

[TestClass]
[TestCategory("Unit")]
public sealed class TaskAssistantAgentServiceTests
{
    [TestMethod]
    public async Task ChatAsync_WithUseToolsFalse_DisablesToolInvocation()
    {
        var chatClient = new CapturingChatClient();
        var agent = CreateAgent(chatClient);

        var response = await agent.ChatAsync(
            new AgentChatRequest { Message = "Reply OK.", UseTools = false },
            tenantId: null, TestContext.CancellationToken);

        Assert.IsTrue(response.IsConfigured);
        Assert.AreEqual(ChatToolMode.None, chatClient.LastOptions?.ToolMode);
    }

    private static TaskAssistantAgentService CreateAgent(CapturingChatClient chatClient)
    {
        var tools = new TaskItemTools(
            NullLogger<TaskItemTools>.Instance,
            Mock.Of<ITaskItemService>(),
            Mock.Of<ITaskFlowSearchService>(),
            Mock.Of<ITaskFlowReadService>());

        return new TaskAssistantAgentService(
            NullLogger<TaskAssistantAgentService>.Instance,
            chatClient,
            tools);
    }

    private sealed class CapturingChatClient : IChatClient
    {
        internal ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "OK");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
