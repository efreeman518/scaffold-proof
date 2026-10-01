using EF.Tenancy;
using EF.Cache;
using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Cqrs.Features.Categories;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Cqrs;

/// <summary>
/// CQRS-style twin of the service failure-mapping tests: a cancelled search propagates instead of returning
/// an empty last page (S3); a failed save returns a fixed message and never swallows cancellation, and a
/// create that lost the insert race to the same caller id replays or 409s (S4, D-033).
/// Pure-unit tier (Moq only): handlers are constructed directly.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CqrsFailureMappingTests
{
    private const string ProviderText = "Violation of PRIMARY KEY constraint 'PK_Category'. Cannot insert duplicate key in object 'taskflow.Category'.";

    private readonly Mock<IRequestContext<string, Guid?>> _requestContext = new();
    private readonly Mock<ICategoryRepositoryTrxn> _categoryTrxn = new();
    private readonly Mock<ICategoryRepositoryQuery> _categoryQuery = new();
    private readonly Mock<ITenantBoundaryValidator> _tenantBoundary = new();

    /// <summary>Prepares a tenant caller whose tenant-boundary checks pass.</summary>
    [TestInitialize]
    public void Setup()
    {
        _requestContext.SetupGet(x => x.TenantId).Returns(TestConstants.TenantId);
        _requestContext.SetupGet(x => x.Roles).Returns(new List<string>());
        _tenantBoundary
            .Setup(x => x.EnsureTenantBoundary(It.IsAny<Guid?>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Result.Success());
    }

    /// <summary>Verifies a cancelled TaskItem search propagates.</summary>
    [TestMethod]
    public async Task Given_CancelledSearch_When_SearchTaskItems_Then_CancellationPropagates()
    {
        var repoQuery = new Mock<ITaskItemRepositoryQuery>();
        repoQuery.Setup(r => r.SearchTaskItemsAsync(It.IsAny<TaskItemCursorSearchRequest>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var handler = new SearchTaskItemsHandler(_requestContext.Object, repoQuery.Object, _tenantBoundary.Object);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new SearchTaskItemsQuery(new TaskItemCursorSearchRequest { PageSize = 10 }), TestContext.CancellationToken));
    }

    /// <summary>Verifies a provider save failure returns the fixed message, not the provider text.</summary>
    [TestMethod]
    public async Task Given_ProviderSaveFailure_When_CreateCategory_Then_ReturnsFixedMessage()
    {
        var command = ArrangeFailingCreate(new DbUpdateException("An error occurred while saving the entity changes.", new InvalidOperationException(ProviderText)));
        _categoryQuery.Setup(r => r.GetCategoryAsync(It.IsAny<CategoryId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Category?)null);

        var result = await CreateHandler().HandleAsync(command, TestContext.CancellationToken);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(ErrorConstants.ERROR_SAVE_FAILED, result.ErrorMessage);
        Assert.DoesNotContain("PK_Category", string.Join(";", result.Errors));
    }

    /// <summary>Verifies a cancelled save propagates instead of becoming a 400 failure result.</summary>
    [TestMethod]
    public async Task Given_CancelledSave_When_CreateCategory_Then_CancellationPropagates()
    {
        var command = ArrangeFailingCreate(new OperationCanceledException());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            CreateHandler().HandleAsync(command, TestContext.CancellationToken));
    }

    /// <summary>Verifies a create that lost the insert race to a different payload with the same id is a 409.</summary>
    [TestMethod]
    public async Task Given_ConcurrentDifferentCreate_When_SaveLosesRace_Then_ThrowsConflict()
    {
        var command = ArrangeFailingCreate(new DbUpdateException("duplicate", new InvalidOperationException(ProviderText)));
        _categoryQuery.Setup(r => r.GetCategoryAsync(It.IsAny<CategoryId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategoryBuilder().WithName("Someone else's category").Build());

        await Assert.ThrowsExactlyAsync<ConflictException>(() =>
            CreateHandler().HandleAsync(command, TestContext.CancellationToken));
    }

    /// <summary>Verifies a create that lost the insert race to an equivalent payload replays the stored row.</summary>
    [TestMethod]
    public async Task Given_ConcurrentEquivalentCreate_When_SaveLosesRace_Then_ReplaysStoredRow()
    {
        var command = ArrangeFailingCreate(new DbUpdateException("duplicate", new InvalidOperationException(ProviderText)));
        _categoryQuery.Setup(r => r.GetCategoryAsync(It.IsAny<CategoryId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategoryBuilder().Build());

        var result = await CreateHandler().HandleAsync(command, TestContext.CancellationToken);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value!.IsReplay);
    }

    /// <summary>Arranges a create with a caller id whose existence check finds nothing and whose save throws.</summary>
    private CreateCategoryCommand ArrangeFailingCreate(Exception saveFailure)
    {
        _categoryTrxn.Setup(r => r.GetCategoryAsync(It.IsAny<CategoryId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Category?)null);
        _categoryTrxn.Setup(r => r.Create(ref It.Ref<Category>.IsAny));
        _categoryTrxn.Setup(r => r.SaveChangesAsync(It.IsAny<OptimisticConcurrencyWinner>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(saveFailure);
        return new CreateCategoryCommand(new DefaultRequest<CategoryDto>
        {
            Item = new CategoryDto { Id = Guid.CreateVersion7(), Name = "Test Category", Description = "Test description", IsActive = true }
        });
    }

    private CreateCategoryHandler CreateHandler() => new(
        NullLogger<CreateCategoryHandler>.Instance,
        _requestContext.Object,
        _categoryTrxn.Object,
        _categoryQuery.Object,
        _tenantBoundary.Object,
        Mock.Of<ITypedCache>());

    public TestContext TestContext { get; set; } = null!;
}
