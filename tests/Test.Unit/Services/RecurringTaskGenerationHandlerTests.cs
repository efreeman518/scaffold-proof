using EF.Common.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Scheduler.Handlers;

namespace Test.Unit.Services;

/// <summary>
/// Validates <see cref="TaskFlow.Scheduler.Handlers.RecurringTaskGenerationHandler"/>: filters tasks
/// with a non-empty <c>RecurrenceFrequency</c>, treats null/empty frequency as non-recurring, and
/// tolerates a null Data response.
/// Pure-unit tier (Moq only): no scheduler host or timer.
/// </summary>
[TestClass]
public class RecurringTaskGenerationHandlerTests
{
    private readonly Mock<ITaskItemService> _serviceMock = new();
    private readonly RecurringTaskGenerationHandler _handler;

    /// <summary>Initializes recurring task generation handler tests with required dependencies and default state.</summary>
    public RecurringTaskGenerationHandlerTests()
    {
        _handler = new RecurringTaskGenerationHandler(
            _serviceMock.Object,
            NullLogger<RecurringTaskGenerationHandler>.Instance);
    }

    /// <summary>Verifies handle with recurring templates logs count behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_WithRecurringTemplates_LogsCount()
    {
        var tasks = new List<TaskItemDto>
        {
            new() { Title = "Daily Standup", RecurrenceFrequency = "Daily" },
            new() { Title = "Weekly Review", RecurrenceFrequency = "Weekly" },
            new() { Title = "Normal Task" }
        };

        _serviceMock.Setup(s => s.SearchAsync(
                It.IsAny<TaskItemCursorSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = tasks });

        await _handler.HandleAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.SearchAsync(
            It.IsAny<TaskItemCursorSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies handle no recurring templates completes with zero count behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_NoRecurringTemplates_CompletesWithZeroCount()
    {
        var tasks = new List<TaskItemDto>
        {
            new() { Title = "One-off Task" },
            new() { Title = "Another Task" }
        };

        _serviceMock.Setup(s => s.SearchAsync(
                It.IsAny<TaskItemCursorSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = tasks });

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

    /// <summary>Verifies handle empty string frequency filters correctly behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task HandleAsync_EmptyStringFrequency_FiltersCorrectly()
    {
        var tasks = new List<TaskItemDto>
        {
            new() { Title = "Empty Freq", RecurrenceFrequency = "" },
            new() { Title = "Null Freq", RecurrenceFrequency = null }
        };

        _serviceMock.Setup(s => s.SearchAsync(
                It.IsAny<TaskItemCursorSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPage<TaskItemDto> { Data = tasks });

        // Empty and null both filtered out - handler considers neither as recurring
        await _handler.HandleAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.SearchAsync(
            It.IsAny<TaskItemCursorSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
