using EF.AI.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
        var chatClient = new FakeChatClient("OK");
        var agent = CreateAgent(chatClient);

        var response = await agent.ChatAsync(
            new AgentChatRequest { Message = "Reply OK.", UseTools = false },
            tenantId: null, TestContext.CancellationToken);

        Assert.IsTrue(response.IsConfigured);
        Assert.AreEqual(ChatToolMode.None, chatClient.LastOptions?.ToolMode);
    }

    private static TaskAssistantAgentService CreateAgent(IChatClient chatClient)
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

    public TestContext TestContext { get; set; } = null!;
}
