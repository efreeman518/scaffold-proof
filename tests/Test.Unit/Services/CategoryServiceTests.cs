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
/// Validates <see cref="TaskFlow.Application.Services.CategoryService"/> orchestration logic with
/// fully-mocked dependencies (repositories, request context, tenant-boundary validator, cache):
/// CRUD success/failure paths, idempotent delete, and search-result passthrough.
/// Pure-unit tier (Moq only): the service contract is exercised in isolation; SQL semantics are covered
/// by the E2E and Integration tiers separately.
/// </summary>
[TestClass]
public class CategoryServiceTests
{
    private readonly Mock<ICategoryRepositoryTrxn> _repoTrxnMock = new();
    private readonly Mock<ICategoryRepositoryQuery> _repoQueryMock = new();
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
    private CategoryService CreateService() => new(
        NullLogger<CategoryService>.Instance,
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
        _repoTrxnMock.Setup(r => r.Create(ref It.Ref<Category>.IsAny));
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new CategoryDto { Name = "Test Category", Description = "Desc" };
        var result = await CreateService().CreateAsync(new DefaultRequest<CategoryDto> { Item = dto }, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Test Category", result.Value!.Item!.Name);
    }

    /// <summary>Verifies that given invalid DTO, when create, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_InvalidDto_When_CreateAsync_Then_ReturnsFailure()
    {
        var dto = new CategoryDto { Name = "" };
        var result = await CreateService().CreateAsync(new DefaultRequest<CategoryDto> { Item = dto }, TestContext.CancellationToken);

        Assert.IsTrue(result.IsFailure);
    }

    /// <summary>Verifies that given existing entity, when get, then returns mapped DTO.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_GetAsync_Then_ReturnsMappedDto()
    {
        var entity = new CategoryBuilder().Build();
        _repoQueryMock.Setup(r => r.GetCategoryAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var result = await CreateService().GetAsync(entity.Id, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(entity.Name, result.Value!.Item!.Name);
    }

    /// <summary>Verifies that given non existent ID, when get, then returns none.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_GetAsync_Then_ReturnsNone()
    {
        _repoQueryMock.Setup(r => r.GetCategoryAsync(It.IsAny<CategoryId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Category?)null);

        var result = await CreateService().GetAsync(Guid.NewGuid(), TestContext.CancellationToken);

        Assert.IsTrue(result.IsNone);
    }

    /// <summary>Verifies that given existing entity, when update, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_UpdateAsync_Then_ReturnsSuccess()
    {
        var entity = new CategoryBuilder().Build();
        _repoTrxnMock.Setup(r => r.GetCategoryAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);
        _repoTrxnMock.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var dto = new CategoryDto { Id = entity.Id, Name = "Updated Name" };
        var result = await CreateService().UpdateAsync(new DefaultRequest<CategoryDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Updated Name", result.Value!.Item!.Name);
    }

    /// <summary>Verifies that given non existent ID, when update, then returns null item.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_UpdateAsync_Then_ReturnsNullItem()
    {
        _repoTrxnMock.Setup(r => r.GetCategoryAsync(It.IsAny<CategoryId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Category?)null);

        var dto = new CategoryDto { Id = Guid.NewGuid(), Name = "Updated" };
        var result = await CreateService().UpdateAsync(new DefaultRequest<CategoryDto> { Item = dto }, null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Value?.Item);
    }

    /// <summary>Verifies that given existing entity, when delete, then returns success.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_DeleteAsync_Then_ReturnsSuccess()
    {
        var entity = new CategoryBuilder().Build();
        _repoTrxnMock.Setup(r => r.GetCategoryAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);
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
        _repoTrxnMock.Setup(r => r.GetCategoryAsync(It.IsAny<CategoryId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Category?)null);

        var result = await CreateService().DeleteAsync(Guid.NewGuid(), null, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
    }

    /// <summary>Verifies that given search request, when search, then returns paged response.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_SearchRequest_When_SearchAsync_Then_ReturnsPagedResponse()
    {
        var dtos = new List<CategoryDto> { new() { Name = "Test" }, new() { Name = "Second" } };
        var pagedResponse = new PagedResponse<CategoryDto> { Data = dtos, Total = 2, PageSize = 10, PageIndex = 0 };
        _repoQueryMock.Setup(r => r.SearchCategoriesAsync(It.IsAny<SearchRequest<CategorySearchFilter>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResponse);

        var request = new SearchRequest<CategorySearchFilter> { PageSize = 10, PageIndex = 0 };
        var response = await CreateService().SearchAsync(request, false, TestContext.CancellationToken);

        Assert.AreEqual(2, response.Total);
        Assert.HasCount(2, response.Data);
    }

    public TestContext TestContext { get; set; } = null!;
}
