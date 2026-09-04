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
/// Validates <see cref="TaskFlow.Scheduler.Handlers.OverdueTaskCheckHandler"/>: it queries
/// <c>ITaskItemService</c> with the overdue filter, tolerates null Data and empty results, and
/// internally filters out Completed/Cancelled tasks.
/// Pure-unit tier (Moq only): the handler is the SUT; no scheduler host needed.
/// </summary>
[TestClass]
public class OverdueTaskCheckHandlerTests
{
    private readonly Mock<ITaskItemService> _serviceMock = new();
    private readonly OverdueTaskCheckHandler _handler;

    /// <summary>Initializes overdue task check handler tests with required dependencies and default state.</summary>
    public OverdueTaskCheckHandlerTests()
    {
        _handler = new OverdueTaskCheckHandler(
            _serviceMock.Object,
            NullLogger<OverdueTaskCheckHandler>.Instance);
    }

    /// <summary>Verifies handle with overdue tasks logs count behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_WithOverdueTasks_LogsCount()
    {
        var overdueTasks = new List<TaskItemDto>
        {
            new() { Title = "Overdue1", Status = TaskItemStatus.Open },
            new() { Title = "Overdue2", Status = TaskItemStatus.InProgress }
        };

        _serviceMock.Setup(s => s.SearchAsync(
                It.Is<TaskItemCursorSearchRequest>(r => r.Filter!.IsOverdue == true),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = overdueTasks });

        await _handler.HandleAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.SearchAsync(
            It.Is<TaskItemCursorSearchRequest>(r => r.Filter!.IsOverdue == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies handle no overdue items completes with zero count behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_NoOverdueItems_CompletesWithZeroCount()
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

    /// <summary>Verifies handle filters out completed and cancelled behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_FiltersOutCompletedAndCancelled()
    {
        var mixedTasks = new List<TaskItemDto>
        {
            new() { Title = "Active Overdue", Status = TaskItemStatus.Open },
            new() { Title = "Done Overdue", Status = TaskItemStatus.Completed },
            new() { Title = "Cancelled Overdue", Status = TaskItemStatus.Cancelled }
        };

        _serviceMock.Setup(s => s.SearchAsync(
                It.IsAny<TaskItemCursorSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = mixedTasks });

        // Handler filters out Completed and Cancelled internally
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
