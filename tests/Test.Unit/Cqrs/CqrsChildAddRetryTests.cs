using EF.Common.Contracts;
using EF.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Cqrs;

/// <summary>
/// D-073: the CQRS child adds (no If-Match) run their read, decision and save inside
/// <c>IRepositoryBase.RetryOnConcurrencyAsync</c>, so a lost save re-reads instead of answering 412. The race itself is
/// proven on both providers by <c>Test.Integration/ChildAddConcurrencyTests</c>; this fast check fails if a handler
/// stops going through the wrapper.
/// Pure-unit tier (Moq only): handlers are constructed directly.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CqrsChildAddRetryTests
{
    private readonly Mock<IRequestContext<string, Guid?>> _requestContext = new();
    private readonly Mock<ITaskItemRepositoryTrxn> _repoTrxn = new();
    private readonly Mock<ITenantBoundaryValidator> _tenantBoundary = new();
    private readonly TaskItem _task = new TaskItemBuilder().Build();

    /// <summary>A tenant caller whose boundary checks pass, and a stored root with no children.</summary>
    [TestInitialize]
    public void Setup()
    {
        _requestContext.SetupGet(x => x.TenantId).Returns(TestConstants.TenantId);
        _requestContext.SetupGet(x => x.Roles).Returns(new List<string>());
        _tenantBoundary
            .Setup(x => x.EnsureTenantBoundary(It.IsAny<Guid?>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Result.Success());
        _repoTrxn.Setup(r => r.GetTaskItemAsync(_task.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(_task);
    }

    /// <summary>The comment add saves once, from inside the retry wrapper.</summary>
    [TestMethod]
    public async Task AddComment_RunsThroughRetryOnConcurrency()
    {
        RunWorkInsideRetry<CommentDto>();
        var handler = new AddTaskItemCommentHandler(
            NullLogger<AddTaskItemCommentHandler>.Instance, _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object);

        var result = await handler.HandleAsync(
            new AddTaskItemCommentCommand(_task.Id.Value, new CommentDto { Body = "retry-wrapped" }), TestContext.CancellationToken);

        AssertSavedInsideRetry<CommentDto>(result);
    }

    /// <summary>The checklist add saves once, from inside the retry wrapper.</summary>
    [TestMethod]
    public async Task AddChecklistItem_RunsThroughRetryOnConcurrency()
    {
        RunWorkInsideRetry<ChecklistItemDto>();
        var handler = new AddTaskItemChecklistItemHandler(
            NullLogger<AddTaskItemChecklistItemHandler>.Instance, _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object);

        var result = await handler.HandleAsync(
            new AddTaskItemChecklistItemCommand(_task.Id.Value, new ChecklistItemDto { Title = "retry-wrapped", SortOrder = 1 }),
            TestContext.CancellationToken);

        AssertSavedInsideRetry<ChecklistItemDto>(result);
    }

    /// <summary>The tag association saves once, from inside the retry wrapper.</summary>
    [TestMethod]
    public async Task AssociateTag_RunsThroughRetryOnConcurrency()
    {
        RunWorkInsideRetry<TaskItemTagDto>();
        var handler = new AssociateTaskItemTagHandler(
            NullLogger<AssociateTaskItemTagHandler>.Instance, _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object);

        var result = await handler.HandleAsync(
            new AssociateTaskItemTagCommand(_task.Id.Value, Guid.CreateVersion7()), TestContext.CancellationToken);

        AssertSavedInsideRetry<TaskItemTagDto>(result);
    }

    /// <summary>The mocked wrapper runs the handler's work once, as when no race is lost.</summary>
    private void RunWorkInsideRetry<TDto>() =>
        _repoTrxn.Setup(r => r.RetryOnConcurrencyAsync(
                It.IsAny<Func<CancellationToken, Task<Result<DefaultResponse<TDto>>>>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<Result<DefaultResponse<TDto>>>> work, int _, CancellationToken token) => work(token));

    private void AssertSavedInsideRetry<TDto>(Result<DefaultResponse<TDto>> result)
    {
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsNotNull(result.Value!.Item);
        _repoTrxn.Verify(r => r.RetryOnConcurrencyAsync(
            It.IsAny<Func<CancellationToken, Task<Result<DefaultResponse<TDto>>>>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _repoTrxn.Verify(r => r.SaveChildAddAsync(It.IsAny<Func<CancellationToken, Task<bool>>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    public TestContext TestContext { get; set; } = null!;
}
