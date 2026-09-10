using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using EF.Common.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.AI.Search;

namespace Test.Unit.AI;

/// <summary>
/// Validates the search fallback used when AI Search is not configured. It no longer returns nothing: it
/// answers from the SQL title-prefix search, so a local run and the AI task tool still get real results.
/// Index and remove stay no-throw no-ops because there is no index to maintain.
/// Pure-unit tier: the repository is mocked.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class NoOpSearchServiceTests
{
    private readonly Mock<ITaskItemRepositoryQuery> _repoMock = new();
    private readonly NoOpSearchService _service;

    /// <summary>Initializes no op search service tests with required dependencies and default state.</summary>
    public NoOpSearchServiceTests()
    {
        _service = new NoOpSearchService(_repoMock.Object, NullLogger<NoOpSearchService>.Instance);
    }

    /// <summary>The query becomes the prefix filter, the tenant is carried through, and maxResults is the page size.</summary>
    [TestMethod]
    public async Task SearchTaskItemsAsync_DelegatesToThePrefixSearch()
    {
        var tenantId = Guid.NewGuid();
        var taskId = Guid.CreateVersion7();
        _repoMock.Setup(r => r.SearchTaskItemsAsync(
                It.IsAny<TaskItemCursorSearchRequest>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto>(
            [
                new() { Id = taskId, Title = "Quarterly report", Status = TaskItemStatus.Open, Priority = Priority.High }
            ], null, false));

        var results = await _service.SearchTaskItemsAsync(
            "Quarter", SearchMode.Keyword, tenantId, maxResults: 5, ct: TestContext.CancellationToken);

        Assert.HasCount(1, results);
        Assert.AreEqual(taskId.ToString(), results[0].Id);
        Assert.AreEqual("Quarterly report", results[0].Title);

        _repoMock.Verify(r => r.SearchTaskItemsAsync(
            It.Is<TaskItemCursorSearchRequest>(q =>
                q.Filter!.SearchTerm == "Quarter" && q.Filter.TenantId == tenantId && q.PageSize == 5),
            tenantId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A maxResults outside the page-size range is clamped rather than rejected.</summary>
    [TestMethod]
    public async Task SearchTaskItemsAsync_ClampsPageSize()
    {
        _repoMock.Setup(r => r.SearchTaskItemsAsync(
                It.IsAny<TaskItemCursorSearchRequest>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto>([], null, false));

        await _service.SearchTaskItemsAsync("x", SearchMode.Semantic, null, maxResults: 0, ct: TestContext.CancellationToken);

        // No tenant on the request means Guid.Empty as the cursor scope: the codec still binds one, and an
        // unscoped search is a caller mistake, not a licence to page across tenants.
        _repoMock.Verify(r => r.SearchTaskItemsAsync(
            It.Is<TaskItemCursorSearchRequest>(q => q.PageSize == PageSizeLimits.Min),
            Guid.Empty,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies index task item completes without error behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task IndexTaskItemAsync_CompletesWithoutError()
    {
        var doc = new TaskItemSearchDocument
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = Guid.NewGuid().ToString(),
            Title = "Test Task",
            Status = "Open",
            Priority = "High",
            LastUpdated = DateTimeOffset.UtcNow
        };

        await _service.IndexTaskItemAsync(doc, TestContext.CancellationToken);
        // No exception = success
    }

    /// <summary>Verifies remove task item completes without error behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task RemoveTaskItemAsync_CompletesWithoutError()
    {
        await _service.RemoveTaskItemAsync(Guid.NewGuid().ToString(), TestContext.CancellationToken);
        // No exception = success
    }

    public TestContext TestContext { get; set; } = null!;
}
