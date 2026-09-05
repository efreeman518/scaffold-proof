using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="TaskFlow.Application.Services.TagService"/> orchestration with mocked
/// repositories, request context, tenant-boundary validator, and cache: CRUD success/failure paths and
/// idempotent delete.
/// Pure-unit tier (Moq only): no real EF - service contract is the SUT.
/// </summary>
[TestClass]
public class TagServiceTests
{
    private readonly Mock<IRepositoryTrxn<Tag, TagId>> _repoTrxnMock = new();
    private readonly Mock<ITagRepositoryQuery> _repoQueryMock = new();
    private readonly Mock<IRequestContext<string, Guid?>> _requestContextMock = new();
    private readonly Mock<ITenantBoundaryValidator> _tenantBoundaryValidatorMock = new();
    private readonly Mock<ITaskFlowCache> _cacheMock = new();

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
    private TagService CreateService() => new(
        NullLogger<TagService>.Instance,
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
        _repoTrxnMock.Setup(r => r.Create(ref It.Ref<Tag>.IsAny));
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new TagDto { Name = "Important", Color = "#FF0000" };
        var result = await CreateService().CreateAsync(new DefaultRequest<TagDto> { Item = dto }, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Important", result.Value!.Item!.Name);
    }

    /// <summary>Verifies that given invalid DTO, when create, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_InvalidDto_When_CreateAsync_Then_ReturnsFailure()
    {
        var dto = new TagDto { Name = "" };
        var result = await CreateService().CreateAsync(new DefaultRequest<TagDto> { Item = dto }, TestContext.CancellationToken);

        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given existing entity, when get, then returns mapped DTO.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_GetAsync_Then_ReturnsMappedDto()
    {
        var entity = new TagBuilder().Build();
        _repoQueryMock.Setup(r => r.GetTagAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var result = await CreateService().GetAsync(entity.Id, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(entity.Name, result.Value!.Item!.Name);
    }

    /// <summary>Verifies that given non existent ID, when get, then returns none.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_GetAsync_Then_ReturnsNone()
    {
        _repoQueryMock.Setup(r => r.GetTagAsync(It.IsAny<TagId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tag?)null);

        var result = await CreateService().GetAsync(Guid.NewGuid(), TestContext.CancellationToken);

        Assert.IsTrue(result.IsNone);
    }

    /// <summary>Verifies that given existing entity, when update, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_UpdateAsync_Then_ReturnsSuccess()
    {
        var entity = new TagBuilder().Build();
        _repoTrxnMock.Setup(r => r.GetAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new TagDto { Id = entity.Id, Name = "Updated", Color = "#00FF00" };
        var result = await CreateService().UpdateAsync(new DefaultRequest<TagDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Updated", result.Value!.Item!.Name);
    }

    /// <summary>Verifies that given non existent ID, when update, then returns null item.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_UpdateAsync_Then_ReturnsNullItem()
    {
        _repoTrxnMock.Setup(r => r.GetAsync(It.IsAny<TagId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tag?)null);

        var dto = new TagDto { Id = Guid.NewGuid(), Name = "Updated" };
        var result = await CreateService().UpdateAsync(new DefaultRequest<TagDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Value?.Item);
    }

    /// <summary>Verifies that given existing entity, when delete, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_DeleteAsync_Then_ReturnsSuccess()
    {
        var entity = new TagBuilder().Build();
        _repoTrxnMock.Setup(r => r.GetAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);
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
        _repoTrxnMock.Setup(r => r.GetAsync(It.IsAny<TagId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tag?)null);

        var result = await CreateService().DeleteAsync(Guid.NewGuid(), null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
    }

    /// <summary>Verifies that given search request, when search, then returns paged response.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_SearchRequest_When_SearchAsync_Then_ReturnsPagedResponse()
    {
        var dtos = new List<TagDto> { new() { Name = "Test" } };
        var pagedResponse = new PagedResponse<TagDto> { Data = dtos, Total = 1, PageSize = 10, PageIndex = 0 };
        _repoQueryMock.Setup(r => r.SearchTagsAsync(It.IsAny<SearchRequest<TagSearchFilter>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResponse);

        var request = new SearchRequest<TagSearchFilter> { PageSize = 10, PageIndex = 0 };
        var response = await CreateService().SearchAsync(request, false, TestContext.CancellationToken);

        Assert.AreEqual(1, response.Total);
    }

    public TestContext TestContext { get; set; } = null!;
}
