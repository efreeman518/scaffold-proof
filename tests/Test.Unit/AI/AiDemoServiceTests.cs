using EF.Common.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.AI.Demos;

namespace Test.Unit.AI;

/// <summary>
/// Covers the AI demo services at the application boundary: model output parsing, no-op behavior,
/// and the write calls made after a model response is accepted.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class AiDemoServiceTests
{
    /// <summary>No-op chat keeps D4 read/write-safe and reports AI as disabled.</summary>
    [TestMethod]
    public async Task TriageAsync_WithNoOpChatClient_ReturnsNotConfiguredWithoutUpdate()
    {
        var taskId = Guid.NewGuid();
        var taskItemService = new Mock<ITaskItemService>();
        taskItemService
            .Setup(x => x.GetAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto>
            {
                Item = new TaskItemDto { Id = taskId, Title = "Billing outage", Priority = Priority.High }
            }));

        var service = new TaskTriageService(
            NullLogger<TaskTriageService>.Instance,
            new NoOpChatClient(NullLogger<NoOpChatClient>.Instance),
            taskItemService.Object);

        var result = await service.TriageAsync(taskId, apply: true, TestContext.CancellationToken);

        Assert.IsFalse(result.IsConfigured);
        Assert.IsFalse(result.Applied);
        Assert.AreEqual("AI model not configured.", result.Error);
        taskItemService.Verify(x => x.UpdateAsync(It.IsAny<DefaultRequest<TaskItemDto>>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>D4 accepts model JSON embedded in extra text and applies the parsed priority.</summary>
    [TestMethod]
    public async Task TriageAsync_WithParseableModelOutput_AppliesSuggestedPriority()
    {
        var taskId = Guid.NewGuid();
        var task = new TaskItemDto
        {
            Id = taskId,
            Title = "Billing outage",
            Description = "Customers cannot pay invoices.",
            Priority = Priority.Low
        };
        var taskItemService = new Mock<ITaskItemService>();
        taskItemService
            .Setup(x => x.GetAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto> { Item = task }));
        taskItemService
            .Setup(x => x.UpdateAsync(It.IsAny<DefaultRequest<TaskItemDto>>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DefaultRequest<TaskItemDto> request, CancellationToken _) =>
                Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto> { Item = request.Item }));

        var chatClient = new StaticChatClient("""
            ```json
            {"suggestedPriority":"Critical","suggestedCategory":"Incident","confidence":0.94,"rationale":"Payment outage"}
            ```
            """);
        var service = new TaskTriageService(
            NullLogger<TaskTriageService>.Instance,
            chatClient,
            taskItemService.Object);

        var result = await service.TriageAsync(taskId, apply: true, TestContext.CancellationToken);

        Assert.IsTrue(result.IsConfigured);
        Assert.IsTrue(result.Applied);
        Assert.AreEqual("Critical", result.Triage!.SuggestedPriority);
        Assert.IsNotNull(chatClient.LastOptions);
        Assert.AreEqual(128, chatClient.LastOptions.MaxOutputTokens);
        taskItemService.Verify(x => x.UpdateAsync(
            It.Is<DefaultRequest<TaskItemDto>>(request => request.Item.Priority == Priority.Critical),
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>D4 rejects parsed JSON when required model fields are null or invalid.</summary>
    [TestMethod]
    public async Task TriageAsync_WithNullSuggestedPriority_ReturnsParseGuardError()
    {
        var taskId = Guid.NewGuid();
        var taskItemService = new Mock<ITaskItemService>();
        taskItemService
            .Setup(x => x.GetAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto>
            {
                Item = new TaskItemDto { Id = taskId, Title = "Review queue", Priority = Priority.Low }
            }));

        var service = new TaskTriageService(
            NullLogger<TaskTriageService>.Instance,
            new StaticChatClient("""{"suggestedPriority":null,"confidence":0.7}"""),
            taskItemService.Object);

        var result = await service.TriageAsync(taskId, apply: true, TestContext.CancellationToken);

        Assert.IsTrue(result.IsConfigured);
        Assert.IsFalse(result.Applied);
        Assert.IsNull(result.Triage);
        Assert.AreEqual("Could not parse model output as triage JSON.", result.Error);
        taskItemService.Verify(x => x.UpdateAsync(It.IsAny<DefaultRequest<TaskItemDto>>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>D5 turns parseable model output into a normal task create request.</summary>
    [TestMethod]
    public async Task DraftAndCreateAsync_WithParseableModelOutput_CreatesTask()
    {
        var createdId = Guid.NewGuid();
        var taskItemService = new Mock<ITaskItemService>();
        taskItemService
            .Setup(x => x.CreateAsync(It.IsAny<DefaultRequest<TaskItemDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DefaultRequest<TaskItemDto> request, CancellationToken _) =>
                Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto>
                {
                    Item = request.Item with { Id = createdId }
                }));

        var service = new TaskDraftService(
            NullLogger<TaskDraftService>.Instance,
            new StaticChatClient("""
                Draft:
                {"description":"Prepare the quarterly compliance summary for leadership.","acceptanceCriteria":"- Summary reviewed\n- Findings attached"}
                """),
            taskItemService.Object);

        var result = await service.DraftAndCreateAsync("Prepare compliance summary", TestContext.CancellationToken);

        Assert.IsTrue(result.IsConfigured);
        Assert.IsTrue(result.Created);
        Assert.AreEqual(createdId, result.TaskId);
        taskItemService.Verify(x => x.CreateAsync(
            It.Is<DefaultRequest<TaskItemDto>>(request =>
                request.Item.Title == "Prepare compliance summary" &&
                request.Item.Description!.Contains("Acceptance criteria:", StringComparison.Ordinal) &&
                request.Item.Description.Contains("Findings attached", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Small deterministic chat client for parser tests.</summary>
    private sealed class StaticChatClient(string responseText) : IChatClient
    {
        internal ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
