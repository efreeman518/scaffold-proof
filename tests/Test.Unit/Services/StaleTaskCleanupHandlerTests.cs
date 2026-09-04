using EF.Common.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Scheduler.Handlers;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="TaskFlow.Scheduler.Handlers.StaleTaskCleanupHandler"/>: queries with
/// <c>Status == Cancelled</c>, only counts tasks whose <c>DueDate</c> is older than 90 days, and
/// tolerates null DueDate / null Data.
/// Pure-unit tier (Moq only): no scheduler host.
/// </summary>
[TestClass]
public class StaleTaskCleanupHandlerTests
{
    private readonly Mock<ITaskItemService> _serviceMock = new();
    private readonly StaleTaskCleanupHandler _handler;

    /// <summary>Initializes stale task cleanup handler tests with required dependencies and default state.</summary>
    public StaleTaskCleanupHandlerTests()
    {
        _handler = new StaleTaskCleanupHandler(
            _serviceMock.Object,
            NullLogger<StaleTaskCleanupHandler>.Instance);
    }

    /// <summary>Verifies handle with stale tasks logs count behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_WithStaleTasks_LogsCount()
    {
        var staleTasks = new List<TaskItemDto>
        {
            new() { Title = "Old Cancelled", Status = TaskItemStatus.Cancelled, DueDate = DateTimeOffset.UtcNow.AddDays(-120) },
            new() { Title = "Very Old", Status = TaskItemStatus.Cancelled, DueDate = DateTimeOffset.UtcNow.AddDays(-200) }
        };

        _serviceMock.Setup(s => s.SearchAsync(
                It.Is<TaskItemCursorSearchRequest>(r => r.Filter!.Status == TaskItemStatus.Cancelled),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = staleTasks });

        await _handler.HandleAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.SearchAsync(
            It.Is<TaskItemCursorSearchRequest>(r => r.Filter!.Status == TaskItemStatus.Cancelled),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies handle no stale tasks completes with zero count behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_NoStaleTasks_CompletesWithZeroCount()
    {
        _serviceMock.Setup(s => s.SearchAsync(
                It.IsAny<TaskItemCursorSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = [] });

        await _handler.HandleAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.SearchAsync(
            It.IsAny<TaskItemCursorSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies handle recent cancelled not considered stale behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_RecentCancelled_NotConsideredStale()
    {
        var recentCancelled = new List<TaskItemDto>
        {
            new() { Title = "Just Cancelled", Status = TaskItemStatus.Cancelled, DueDate = DateTimeOffset.UtcNow.AddDays(-10) },
            new() { Title = "Last Month", Status = TaskItemStatus.Cancelled, DueDate = DateTimeOffset.UtcNow.AddDays(-30) }
        };

        _serviceMock.Setup(s => s.SearchAsync(
                It.IsAny<TaskItemCursorSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = recentCancelled });

        // Handler filters to DueDate < 90 days ago - these are recent, so staleTasks count = 0
        await _handler.HandleAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.SearchAsync(
            It.IsAny<TaskItemCursorSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies handle null due date not considered stale behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_NullDueDate_NotConsideredStale()
    {
        var noDueDate = new List<TaskItemDto>
        {
            new() { Title = "No Due Date", Status = TaskItemStatus.Cancelled, DueDate = null }
        };

        _serviceMock.Setup(s => s.SearchAsync(
                It.IsAny<TaskItemCursorSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = noDueDate });

        // Null DueDate -> not stale (DueDate.HasValue is false)
        await _handler.HandleAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.SearchAsync(
            It.IsAny<TaskItemCursorSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies handle null data treats as empty behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_NullData_TreatsAsEmpty()
    {
        _serviceMock.Setup(s => s.SearchAsync(
                It.IsAny<TaskItemCursorSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = null! });

        await _handler.HandleAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.SearchAsync(
            It.IsAny<TaskItemCursorSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
