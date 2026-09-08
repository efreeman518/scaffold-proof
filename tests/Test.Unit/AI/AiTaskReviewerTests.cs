using EF.Common.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FeatureManagement;
using Moq;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Infrastructure.AI.Demos;

namespace Test.Unit.AI;

/// <summary>
/// D-042: AiTaskReviewer checks the AiReview flag via IVariantFeatureManager before spending a model
/// call, so a disabled flag behaves like the existing no-op-chat-client skip path.
/// Pure-unit tier (Moq doubles): no live model, no database.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class AiTaskReviewerTests
{
    [TestMethod]
    public async Task ReviewNewTaskAsync_AiReviewFlagOff_SkipsWithoutLoadingTask()
    {
        var featureManager = new Mock<IVariantFeatureManager>();
        featureManager
            .Setup(x => x.IsEnabledAsync(TaskFlowFeatures.AiReview, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var taskItemService = new Mock<ITaskItemService>();

        var reviewer = new AiTaskReviewer(
            NullLogger<AiTaskReviewer>.Instance,
            Mock.Of<IChatClient>(),
            featureManager.Object,
            taskItemService.Object);

        await reviewer.ReviewNewTaskAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.CancellationToken);

        taskItemService.Verify(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ReviewNewTaskAsync_AiReviewFlagOn_ProceedsToLoadTask()
    {
        var taskId = Guid.NewGuid();
        var featureManager = new Mock<IVariantFeatureManager>();
        featureManager
            .Setup(x => x.IsEnabledAsync(TaskFlowFeatures.AiReview, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var taskItemService = new Mock<ITaskItemService>();
        taskItemService
            .Setup(x => x.GetAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DefaultResponse<TaskItemDto>>.None());

        var reviewer = new AiTaskReviewer(
            NullLogger<AiTaskReviewer>.Instance,
            Mock.Of<IChatClient>(),
            featureManager.Object,
            taskItemService.Object);

        await reviewer.ReviewNewTaskAsync(taskId, Guid.NewGuid(), TestContext.CancellationToken);

        taskItemService.Verify(x => x.GetAsync(taskId, It.IsAny<CancellationToken>()), Times.Once);
    }

    public TestContext TestContext { get; set; } = null!;
}
