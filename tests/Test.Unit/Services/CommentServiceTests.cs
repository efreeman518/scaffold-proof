using EF.Common.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="TaskFlow.Application.Services.CommentService"/> orchestration with mocked
/// dependencies: read-side (search and get) success/failure paths.
/// Pure-unit tier (Moq only).
/// </summary>
[TestClass]
public class CommentServiceTests
{
    private readonly Mock<ICommentRepositoryQuery> _repoQueryMock = new();
    private readonly Mock<IRequestContext<string, Guid?>> _requestContextMock = new();
    private readonly Mock<ITenantBoundaryValidator> _tenantBoundaryValidatorMock = new();

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
    private CommentService CreateService() => new(
        NullLogger<CommentService>.Instance,
        _requestContextMock.Object,
        _repoQueryMock.Object,
        _tenantBoundaryValidatorMock.Object);

    // TODO(phase-c): Create/Update/Delete unit tests were removed because the standalone write path on
    // CommentService was deleted to enforce the TaskItem aggregate boundary. Re-add coverage against the
    // nested TaskItem aggregate write operations once they exist.

    /// <summary>Verifies that given existing entity, when get, then returns mapped DTO.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_ExistingEntity_When_GetAsync_Then_ReturnsMappedDto()
    {
        var entity = new CommentBuilder().Build();
        _repoQueryMock.Setup(r => r.GetCommentAsync(entity.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var result = await CreateService().GetAsync(entity.Id, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(entity.Body, result.Value!.Item!.Body);
    }

    /// <summary>Verifies that given non existent ID, when get, then returns none.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_NonExistentId_When_GetAsync_Then_ReturnsNone()
    {
        _repoQueryMock.Setup(r => r.GetCommentAsync(It.IsAny<CommentId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Comment?)null);

        var result = await CreateService().GetAsync(Guid.NewGuid(), TestContext.CancellationToken);

        Assert.IsTrue(result.IsNone);
    }

    /// <summary>Verifies that given search request, when search, then returns paged response.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Given_SearchRequest_When_SearchAsync_Then_ReturnsPagedResponse()
    {
        var dtos = new List<CommentDto> { new() { Body = "Test" } };
        var pagedResponse = new PagedResponse<CommentDto> { Data = dtos, Total = 1, PageSize = 10, PageIndex = 0 };
        _repoQueryMock.Setup(r => r.SearchCommentsAsync(It.IsAny<SearchRequest<CommentSearchFilter>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResponse);

        var request = new SearchRequest<CommentSearchFilter> { PageSize = 10, PageIndex = 0 };
        var response = await CreateService().SearchAsync(request, false, TestContext.CancellationToken);

        Assert.AreEqual(1, response.Total);
    }

    public TestContext TestContext { get; set; } = null!;
}
