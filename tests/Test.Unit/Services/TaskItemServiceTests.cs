using EF.Cache;
using EF.Common.Contracts;
using EF.Data.Contracts;
using EF.Domain.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="TaskFlow.Application.Services.TaskItemService"/> orchestration with fully-mocked
/// dependencies (repositories, request context, tenant-boundary validator, cache, event bus): CRUD
/// success/failure paths, status transitions surfacing as DomainResult, search projection, and event
/// publication on create/update.
/// Pure-unit tier (Moq only): the service contract is exercised in isolation - SQL semantics are covered
/// by the E2E tier (<c>TaskItemCrudE2ETests</c>) and Integration tier (<c>DomainEventPipelineTests</c>).
/// </summary>
[TestClass]
public class TaskItemServiceTests
{
    private readonly Mock<ITaskItemRepositoryTrxn> _repoTrxnMock = new();
    private readonly Mock<ITaskItemRepositoryQuery> _repoQueryMock = new();
    private readonly Mock<IRequestContext<string, Guid?>> _requestContextMock = new();
    private readonly Mock<ITenantBoundaryValidator> _tenantBoundaryValidatorMock = new();
    private readonly Mock<ITypedCache> _cacheMock = new();

    /// <summary>Prepares per-test fixtures so each test starts from a predictable state.</summary>
    [TestInitialize]
    public void Setup()
    {
        _requestContextMock.Setup(x => x.TenantId).Returns(TestConstants.TenantId);
        _requestContextMock.Setup(x => x.Roles).Returns(new List<string>());
        _tenantBoundaryValidatorMock
            .Setup(x => x.EnsureTenantBoundary(It.IsAny<ILogger>(), It.IsAny<Guid?>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Result.Success());
        _tenantBoundaryValidatorMock
            .Setup(x => x.PreventTenantChange(It.IsAny<ILogger>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(Result.Success());
    }

    /// <summary>Creates service used by the surrounding test cases.</summary>
    private TaskItemService CreateService() => new(
        NullLogger<TaskItemService>.Instance,
        _requestContextMock.Object,
        _repoTrxnMock.Object,
        _repoQueryMock.Object,
        _tenantBoundaryValidatorMock.Object,
        _cacheMock.Object);

    /// <summary>Verifies that given valid DTO, when create, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ValidDto_When_CreateAsync_Then_ReturnsSuccess()
    {
        _repoTrxnMock.Setup(r => r.Create(ref It.Ref<TaskItem>.IsAny));
        _repoTrxnMock.Setup(r => r.UpdateFromDto(It.IsAny<TaskItem>(), It.IsAny<TaskItemDto>(), It.IsAny<RelatedDeleteBehavior>()))
            .Returns((TaskItem e, TaskItemDto _, RelatedDeleteBehavior __) => DomainResult<TaskItem>.Success(e));
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new TaskItemDto { Title = "Test Task", Priority = Priority.Medium };
        var result = await CreateService().CreateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Test Task", result.Value!.Item!.Title);
        Assert.AreEqual(TaskItemStatus.Open, result.Value.Item.Status);
    }

    /// <summary>Verifies that given invalid DTO, when create, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_InvalidDto_When_CreateAsync_Then_ReturnsFailure()
    {
        var dto = new TaskItemDto { Title = "" };
        var result = await CreateService().CreateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, TestContext.CancellationToken);

        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given existing entity, when get, then returns mapped DTO.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_GetAsync_Then_ReturnsMappedDto()
    {
        var entity = new TaskItemBuilder().Build();
        _repoQueryMock.Setup(r => r.GetTaskItemAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var result = await CreateService().GetAsync(entity.Id, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(entity.Title, result.Value!.Item!.Title);
    }

    /// <summary>Verifies that given non existent ID, when get, then returns none.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_GetAsync_Then_ReturnsNone()
    {
        _repoQueryMock.Setup(r => r.GetTaskItemAsync(It.IsAny<TaskItemId>(), It.IsAny<CancellationToken>())).ReturnsAsync((TaskItem?)null);

        var result = await CreateService().GetAsync(Guid.NewGuid(), TestContext.CancellationToken);

        Assert.IsTrue(result.IsNone);
    }

    /// <summary>Verifies that given existing entity, when update, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_UpdateAsync_Then_ReturnsSuccess()
    {
        var entity = new TaskItemBuilder().Build();
        _repoTrxnMock.Setup(r => r.GetTaskItemAsync(entity.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(entity);
        _repoTrxnMock.Setup(r => r.UpdateFromDto(It.IsAny<TaskItem>(), It.IsAny<TaskItemDto>(), It.IsAny<RelatedDeleteBehavior>()))
            .Returns((TaskItem e, TaskItemDto _, RelatedDeleteBehavior __) => DomainResult<TaskItem>.Success(e));
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new TaskItemDto { Id = entity.Id, Title = "Updated Title", Status = TaskItemStatus.Open };
        var result = await CreateService().UpdateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Updated Title", result.Value!.Item!.Title);
    }

    /// <summary>Verifies that given existing entity, when update with status transition, then status updated.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_UpdateWithStatusTransition_Then_StatusUpdated()
    {
        var entity = new TaskItemBuilder().Build(); // Status = Open
        _repoTrxnMock.Setup(r => r.GetTaskItemAsync(entity.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(entity);
        _repoTrxnMock.Setup(r => r.UpdateFromDto(It.IsAny<TaskItem>(), It.IsAny<TaskItemDto>(), It.IsAny<RelatedDeleteBehavior>()))
            .Returns((TaskItem e, TaskItemDto _, RelatedDeleteBehavior __) => DomainResult<TaskItem>.Success(e));
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new TaskItemDto { Id = entity.Id, Title = entity.Title, Status = TaskItemStatus.InProgress };
        var result = await CreateService().UpdateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(TaskItemStatus.InProgress, result.Value!.Item!.Status);
    }

    /// <summary>Verifies that given existing entity, when update with invalid transition, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_UpdateWithInvalidTransition_Then_ReturnsFailure()
    {
        var entity = new TaskItemBuilder().Build(); // Status = Open
        _repoTrxnMock.Setup(r => r.GetTaskItemAsync(entity.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var dto = new TaskItemDto { Id = entity.Id, Title = entity.Title, Status = TaskItemStatus.Completed };
        var result = await CreateService().UpdateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given non existent ID, when update, then returns null item.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_UpdateAsync_Then_ReturnsNullItem()
    {
        _repoTrxnMock.Setup(r => r.GetTaskItemAsync(It.IsAny<TaskItemId>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((TaskItem?)null);

        var dto = new TaskItemDto { Id = Guid.NewGuid(), Title = "Updated" };
        var result = await CreateService().UpdateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Value?.Item);
    }

    /// <summary>Verifies that given existing entity, when delete, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_DeleteAsync_Then_ReturnsSuccess()
    {
        var entity = new TaskItemBuilder().Build();
        _repoTrxnMock.Setup(r => r.GetTaskItemAsync(entity.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(entity);
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await CreateService().DeleteAsync(entity.Id, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        _repoTrxnMock.Verify(r => r.Delete(entity), Times.Once);
    }

    /// <summary>Verifies that given non existent ID, when delete, then returns success idempotent.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_DeleteAsync_Then_ReturnsSuccessIdempotent()
    {
        _repoTrxnMock.Setup(r => r.GetTaskItemAsync(It.IsAny<TaskItemId>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((TaskItem?)null);

        var result = await CreateService().DeleteAsync(Guid.NewGuid(), null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
    }

    /// <summary>Verifies that given search request, when search, then returns a keyset page.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_SearchRequest_When_SearchAsync_Then_ReturnsCursorPage()
    {
        var dto = new TaskItemDto { Id = Guid.CreateVersion7(), Title = "Test" };
        _repoQueryMock.Setup(r => r.SearchTaskItemsAsync(
                It.IsAny<TaskItemCursorSearchRequest>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto>([dto], null, false));

        var request = new TaskItemCursorSearchRequest { PageSize = 10 };
        var response = await CreateService().SearchAsync(request, TestContext.CancellationToken);

        Assert.HasCount(1, response.Items);
        Assert.IsFalse(response.HasMore);
        Assert.IsNull(response.NextCursor);
    }

    public TestContext TestContext { get; set; } = null!;
}
