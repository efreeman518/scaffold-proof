using EF.Cache;
using EF.Common.Contracts;
using EF.Data.Contracts;
using EF.Domain.Contracts;
using EF.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Cqrs.Features.Attachments;
using TaskFlow.Application.Cqrs.Features.Categories;
using TaskFlow.Application.Cqrs.Features.Tags;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Cqrs;

/// <summary>
/// D-073: the CQRS writes whose caller stated no precondition run their read, decision and save inside
/// <c>IRepositoryBase.RetryOnConcurrencyAsync</c>, so a lost save re-reads instead of answering 412: the child adds
/// (no If-Match) and every edit or delete sent with <c>If-Match: *</c>. A concrete If-Match runs once, outside the
/// retry, and keeps its 412. The races themselves are proven on both providers by
/// <c>Test.Integration/ChildAddConcurrencyTests</c> and <c>WildcardWriteConcurrencyTests</c>; this fast check fails if a
/// handler stops going through the wrapper.
/// Pure-unit tier (Moq only): handlers are constructed directly.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CqrsConcurrencyRetryTests
{
    private readonly Mock<IRequestContext<string, Guid?>> _requestContext = new();
    private readonly Mock<ITaskItemRepositoryTrxn> _repoTrxn = new();
    private readonly Mock<ICategoryRepositoryTrxn> _categoryRepo = new();
    private readonly Mock<IRepositoryTrxn<Tag, TagId>> _tagRepo = new();
    private readonly Mock<IAttachmentRepositoryTrxn> _attachmentRepo = new();
    private readonly Mock<ITenantBoundaryValidator> _tenantBoundary = new();
    private readonly Mock<ITypedCache> _cache = new();
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
        _tenantBoundary
            .Setup(x => x.PreventTenantChange(It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(Result.Success());
        _repoTrxn.Setup(r => r.GetTaskItemAsync(_task.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_task);
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

    /// <summary>Every If-Match write the CQRS style handles, by the name the data rows use.</summary>
    private static readonly string[] IfMatchWrites =
    [
        "UpdateTaskItem", "PatchTaskItem", "DeleteTaskItem",
        "UpdateComment", "RemoveComment", "UpdateChecklistItem", "RemoveChecklistItem", "RemoveTag",
        "UpdateCategory", "DeleteCategory", "UpdateTag", "DeleteTag", "UpdateAttachment", "DeleteAttachment",
    ];

    /// <summary>The rows for the If-Match tests: one per write in <see cref="IfMatchWrites"/>.</summary>
    public static IEnumerable<object[]> IfMatchWriteRows => IfMatchWrites.Select(w => new object[] { w });

    /// <summary><c>If-Match: *</c> runs the write's read and its one save inside the retry.</summary>
    [TestMethod]
    [DynamicData(nameof(IfMatchWriteRows))]
    public async Task WildcardWrite_SavesInsideRetryOnConcurrency(string write)
    {
        var probe = AttachProbe(write);

        var result = await RunIfMatchWriteAsync(write, expectedVersion: null);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual(1, probe.Retries, "the wildcard write runs through the retry once");
        Assert.AreEqual(1, probe.SavesInside, "its one save is made from inside the retry");
        Assert.AreEqual(0, probe.SavesOutside);
    }

    /// <summary>A concrete If-Match runs the write once with no retry around it, so a lost save stays 412.</summary>
    [TestMethod]
    [DynamicData(nameof(IfMatchWriteRows))]
    public async Task ConcreteIfMatchWrite_SavesWithoutRetry(string write)
    {
        var probe = AttachProbe(write);

        var result = await RunIfMatchWriteAsync(write, expectedVersion: _currentVersion);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual(0, probe.Retries, "a concrete If-Match is never retried");
        Assert.AreEqual(1, probe.SavesOutside);
        Assert.AreEqual(0, probe.SavesInside);
    }

    private long _currentVersion;
    private Func<long?, Task<Result>> _run = null!;

    /// <summary>Stores the entity the write targets, attaches the probe to its repository and builds the call.</summary>
    private RetryProbe AttachProbe(string write)
    {
        var ct = TestContext.CancellationToken;
        var taskId = _task.Id.Value;
        _currentVersion = _task.Version;

        switch (write)
        {
            case "UpdateTaskItem":
                _repoTrxn.Setup(r => r.UpdateFromDto(It.IsAny<TaskItem>(), It.IsAny<TaskItemDto>(), It.IsAny<RelatedDeleteBehavior>()))
                    .Returns((TaskItem e, TaskItemDto _, RelatedDeleteBehavior __) => DomainResult<TaskItem>.Success(e));
                _run = v => Plain(new UpdateTaskItemHandler(Log<UpdateTaskItemHandler>(), _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object, _cache.Object)
                    .HandleAsync(new UpdateTaskItemCommand(new DefaultRequest<TaskItemDto> { Item = _task.ToDto() }, v), ct));
                break;
            case "PatchTaskItem":
                _run = v => Plain(new PatchTaskItemHandler(Log<PatchTaskItemHandler>(), _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object, _cache.Object)
                    .HandleAsync(new PatchTaskItemCommand(taskId, new TaskItemPatchDto { Title = "probed" }, v), ct));
                break;
            case "DeleteTaskItem":
                _run = v => new DeleteTaskItemHandler(Log<DeleteTaskItemHandler>(), _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object, _cache.Object)
                    .HandleAsync(new DeleteTaskItemCommand(taskId, v), ct);
                break;
            case "UpdateComment":
            case "RemoveComment":
                var comment = new CommentBuilder().WithTaskItemId(taskId).Build();
                _repoTrxn.Setup(r => r.GetCommentAsync(_task.Id, comment.Id, It.IsAny<CancellationToken>())).ReturnsAsync(comment);
                _run = write == "UpdateComment"
                    ? v => Plain(new UpdateTaskItemCommentHandler(Log<UpdateTaskItemCommentHandler>(), _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object)
                        .HandleAsync(new UpdateTaskItemCommentCommand(taskId, comment.Id.Value, new CommentDto { Body = "probed" }, v), ct))
                    : v => new RemoveTaskItemCommentHandler(Log<RemoveTaskItemCommentHandler>(), _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object)
                        .HandleAsync(new RemoveTaskItemCommentCommand(taskId, comment.Id.Value, v), ct);
                break;
            case "UpdateChecklistItem":
            case "RemoveChecklistItem":
                var item = new ChecklistItemBuilder().WithTaskItemId(taskId).Build();
                _repoTrxn.Setup(r => r.GetChecklistItemAsync(_task.Id, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
                _run = write == "UpdateChecklistItem"
                    ? v => Plain(new UpdateTaskItemChecklistItemHandler(Log<UpdateTaskItemChecklistItemHandler>(), _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object)
                        .HandleAsync(new UpdateTaskItemChecklistItemCommand(taskId, item.Id.Value, new ChecklistItemDto { Title = "probed", SortOrder = 1 }, v), ct))
                    : v => new RemoveTaskItemChecklistItemHandler(Log<RemoveTaskItemChecklistItemHandler>(), _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object)
                        .HandleAsync(new RemoveTaskItemChecklistItemCommand(taskId, item.Id.Value, v), ct);
                break;
            case "RemoveTag":
                var association = new TaskItemTagBuilder().WithTaskItemId(taskId).Build();
                _repoTrxn.Setup(r => r.GetTaskItemTagAsync(_task.Id, association.TagId, It.IsAny<CancellationToken>())).ReturnsAsync(association);
                _run = v => new RemoveTaskItemTagHandler(Log<RemoveTaskItemTagHandler>(), _requestContext.Object, _repoTrxn.Object, _tenantBoundary.Object)
                    .HandleAsync(new RemoveTaskItemTagCommand(taskId, association.TagId.Value, v), ct);
                break;
            case "UpdateCategory":
            case "DeleteCategory":
                var category = new CategoryBuilder().Build();
                _currentVersion = category.Version;
                _categoryRepo.Setup(r => r.GetCategoryAsync(category.Id, It.IsAny<CancellationToken>())).ReturnsAsync(category);
                _run = write == "UpdateCategory"
                    ? v => Plain(new UpdateCategoryHandler(Log<UpdateCategoryHandler>(), _requestContext.Object, _categoryRepo.Object, _tenantBoundary.Object, _cache.Object)
                        .HandleAsync(new UpdateCategoryCommand(new DefaultRequest<CategoryDto> { Item = new CategoryDtoBuilder().WithId(category.Id.Value).Build() }, v), ct))
                    : v => new DeleteCategoryHandler(Log<DeleteCategoryHandler>(), _requestContext.Object, _categoryRepo.Object, _tenantBoundary.Object, _cache.Object)
                        .HandleAsync(new DeleteCategoryCommand(category.Id.Value, v), ct);
                return new RetryProbe().Attach(_categoryRepo);
            case "UpdateTag":
            case "DeleteTag":
                var tag = new TagBuilder().Build();
                _currentVersion = tag.Version;
                _tagRepo.Setup(r => r.GetAsync(tag.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tag);
                _run = write == "UpdateTag"
                    ? v => Plain(new UpdateTagHandler(Log<UpdateTagHandler>(), _requestContext.Object, _tagRepo.Object, _tenantBoundary.Object, _cache.Object)
                        .HandleAsync(new UpdateTagCommand(new DefaultRequest<TagDto> { Item = new TagDtoBuilder().WithId(tag.Id.Value).Build() }, v), ct))
                    : v => new DeleteTagHandler(Log<DeleteTagHandler>(), _requestContext.Object, _tagRepo.Object, _tenantBoundary.Object, _cache.Object)
                        .HandleAsync(new DeleteTagCommand(tag.Id.Value, v), ct);
                return new RetryProbe().Attach(_tagRepo);
            case "UpdateAttachment":
            case "DeleteAttachment":
                var attachment = new AttachmentBuilder().Build();
                _currentVersion = attachment.Version;
                _attachmentRepo.Setup(r => r.GetAttachmentAsync(attachment.Id, It.IsAny<CancellationToken>())).ReturnsAsync(attachment);
                _run = write == "UpdateAttachment"
                    ? v => Plain(new UpdateAttachmentHandler(Log<UpdateAttachmentHandler>(), _requestContext.Object, _attachmentRepo.Object, _tenantBoundary.Object)
                        .HandleAsync(new UpdateAttachmentCommand(new DefaultRequest<AttachmentDto> { Item = new AttachmentDtoBuilder().WithId(attachment.Id.Value).Build() }, v), ct))
                    : v => new DeleteAttachmentHandler(Log<DeleteAttachmentHandler>(), _requestContext.Object, _attachmentRepo.Object, _tenantBoundary.Object, _cache.Object)
                        .HandleAsync(new DeleteAttachmentCommand(attachment.Id.Value, v), ct);
                return new RetryProbe().Attach(_attachmentRepo);
            default:
                throw new ArgumentOutOfRangeException(nameof(write), write, "no such If-Match write");
        }

        return new RetryProbe().Attach(_repoTrxn);
    }

    private Task<Result> RunIfMatchWriteAsync(string write, long? expectedVersion) => _run(expectedVersion);

    private static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    /// <summary>Drops the response, keeping the outcome and its message.</summary>
    private static async Task<Result> Plain<T>(Task<Result<T>> call)
    {
        var result = await call;
        return result.IsSuccess ? Result.Success() : Result.Failure(result.ErrorMessage ?? string.Join("; ", result.Errors));
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
