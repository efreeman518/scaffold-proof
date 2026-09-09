using EF.Common.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.AI.Agents.Tools;
using TaskFlow.Infrastructure.AI.Search;

namespace Test.Unit.AI;

/// <summary>
/// Validates the agent function-call wrappers in <c>TaskItemTools</c> (search, get details, create,
/// summarize backlog) format their output strings correctly and surface invalid id / not-found cases
/// as user-facing messages.
/// Pure-unit tier (Moq only): the underlying service and search are substituted; tool plumbing is the SUT.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TaskItemToolsTests
{
    private readonly Mock<ITaskItemService> _taskItemServiceMock = new();
    private readonly Mock<ITaskFlowSearchService> _searchServiceMock = new();
    private readonly Mock<ITaskFlowReadService> _readServiceMock = new();
    private TaskItemTools _tools = null!;

    /// <summary>Prepares per-test fixtures so each test starts from a predictable state.</summary>
    [TestInitialize]
    public void Setup()
    {
        _tools = new TaskItemTools(
            NullLogger<TaskItemTools>.Instance,
            _taskItemServiceMock.Object,
            _searchServiceMock.Object,
            _readServiceMock.Object);
    }

    /// <summary>Verifies search tasks returns formatted results behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SearchTasks_ReturnsFormattedResults()
    {
        var searchResults = new List<TaskItemSearchResult>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Fix login bug",
                Status = "Open",
                Priority = "High",
                DueDate = DateTimeOffset.UtcNow.AddDays(3)
            }
        };

        _searchServiceMock
            .Setup(x => x.SearchTaskItemsAsync("login", SearchMode.Hybrid, null, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(searchResults);

        var result = await _tools.SearchTasks("login");

        Assert.Contains("Fix login bug", result);
        Assert.Contains("High", result);
    }

    /// <summary>Verifies search tasks no results returns not found message behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SearchTasks_NoResults_ReturnsNotFoundMessage()
    {
        _searchServiceMock
            .Setup(x => x.SearchTaskItemsAsync(It.IsAny<string>(), It.IsAny<SearchMode>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TaskItemSearchResult>());

        var result = await _tools.SearchTasks("nonexistent");

        Assert.AreEqual("No tasks found matching your query.", result);
    }

    /// <summary>Verifies get task details returns task info behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task GetTaskDetails_ReturnsTaskInfo()
    {
        var taskId = Guid.NewGuid();
        var dto = new TaskItemDto
        {
            Id = taskId,
            Title = "Important Task",
            Status = TaskItemStatus.InProgress,
            Priority = Priority.High,
            Description = "Do important things"
        };

        _taskItemServiceMock
            .Setup(x => x.GetAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto> { Item = dto }));

        var result = await _tools.GetTaskDetails(taskId.ToString());

        Assert.Contains("Important Task", result);
        Assert.Contains("InProgress", result);
    }

    /// <summary>Verifies get task details invalid ID returns error behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task GetTaskDetails_InvalidId_ReturnsError()
    {
        var result = await _tools.GetTaskDetails("not-a-guid");

        Assert.Contains("Invalid task ID", result);
    }

    /// <summary>Verifies get task details not found returns not found behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task GetTaskDetails_NotFound_ReturnsNotFound()
    {
        var taskId = Guid.NewGuid();

        _taskItemServiceMock
            .Setup(x => x.GetAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DefaultResponse<TaskItemDto>>.None());

        var result = await _tools.GetTaskDetails(taskId.ToString());

        Assert.Contains("not found", result);
    }

    /// <summary>Creates task calls service returns confirmation used by the surrounding test cases.</summary>
    [TestMethod]
    public async Task CreateTask_CallsService_ReturnsConfirmation()
    {
        var newId = Guid.NewGuid();
        _taskItemServiceMock
            .Setup(x => x.CreateAsync(It.IsAny<DefaultRequest<TaskItemDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DefaultResponse<TaskItemDto>>.Success(
                new DefaultResponse<TaskItemDto> { Item = new TaskItemDto { Id = newId, Title = "New task" } }));

        var result = await _tools.CreateTask("New task", "A description", "High");

        Assert.Contains("Created task", result);
        Assert.Contains(newId.ToString(), result);
    }

    /// <summary>Verifies summarize backlog returns status breakdown behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SummarizeBacklog_ReturnsStatusBreakdown()
    {
        var summary = new TaskItemSummaryDto
        {
            Total = 4,
            Overdue = 1,
            ByStatus =
            [
                new TaskItemStatusCountDto(TaskItemStatus.Open, 2),
                new TaskItemStatusCountDto(TaskItemStatus.InProgress, 1),
                new TaskItemStatusCountDto(TaskItemStatus.Completed, 1)
            ]
        };

        _readServiceMock
            .Setup(x => x.GetTaskItemSummaryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var result = await _tools.SummarizeBacklog();

        Assert.Contains("4 total", result);
        Assert.Contains("Open: 2", result);
        Assert.Contains("InProgress: 1", result);
        Assert.Contains("Overdue: 1", result);
    }

    /// <summary>Verifies a concurrent edit between the read and write surfaces as a retry message, not an unhandled exception.</summary>
    [TestMethod]
    public async Task UpdateTaskStatus_ConcurrencyMismatch_ReturnsRetryMessage()
    {
        var taskId = Guid.NewGuid();
        var dto = new TaskItemDto { Id = taskId, Title = "Racy task", Status = TaskItemStatus.Open, Version = 1 };

        _taskItemServiceMock
            .Setup(x => x.GetAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto> { Item = dto }));
        _taskItemServiceMock
            .Setup(x => x.UpdateAsync(It.IsAny<DefaultRequest<TaskItemDto>>(), dto.Version, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyMismatchException("TaskItem", taskId, dto.Version, (dto.Version ?? 0) + 1));

        var result = await _tools.UpdateTaskStatus(taskId.ToString(), "InProgress");

        Assert.Contains("retry", result, StringComparison.OrdinalIgnoreCase);
    }
}
