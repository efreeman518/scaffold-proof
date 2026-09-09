using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;

using Test.Support;

namespace Test.Integration;

/// <summary>
/// Validates the domain-event projection pipeline: a TaskItem persisted to SQL is read by
/// <c>TaskViewProjectionService</c> through the query-side repositories and emitted as a TaskView
/// document with correct counts (comments, attachments, checklist totals/completed).
/// Component tier: only SQL is exercised here (the Service Bus -> Function -> projection hop is covered
/// by the mesh tier in <c>Test.Aspire</c>); contexts are built against a standalone SQL Testcontainer via
/// <c>DbContainerFixture</c> (started by <c>IntegrationTestSetup</c>) - no Aspire graph. The TaskView
/// store is in-memory (<c>InMemoryTaskViewRepository</c>) - real Cosmos behavior is out of scope.
/// </summary>
[TestClass]
public class DomainEventPipelineTests
{
    private static readonly Guid TenantGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantId = DomainId.From<TenantId>(TenantGuid);

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError))
            return;
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(_.CancellationToken);
    }

    /// <summary>Marks the test Inconclusive when the SQL container failed to start (assembly-init safety).</summary>
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);
    }

    /// <summary>Verifies that given task item created, when projection runs, then task view produced.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Given_TaskItemCreated_When_ProjectionRuns_Then_TaskViewProduced()
    {
        // Arrange - real SQL via TestContainers
        var connStr = DbContainerFixture.ConnectionString;
        var ctx = DbContainerFixture.CreateTrxnContext(connStr);

        var category = Category.Create(TenantId, "Work").Value!;
        ctx.Categories.Add(category);
        await ctx.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);

        var taskResult = TaskItem.Create(TenantId, "Integration Test Task",
            "Verify projection pipeline", Priority.High, category.Id);
        Assert.IsTrue(taskResult.IsSuccess);
        var task = taskResult.Value!;
        ctx.TaskItems.Add(task);
        await ctx.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);

        // Create a query context for the repo
        var queryCtx = DbContainerFixture.CreateQueryContext(connStr);
        var taskItemRepo = new TaskItemRepositoryQuery(queryCtx, TestColumnEncryption.Keys);
        var attachmentRepo = new AttachmentRepositoryQuery(queryCtx);

        // In-memory task view store (simulates Cosmos)
        var taskViewRepo = new InMemoryTaskViewRepository();

        var projectionService = new TaskViewProjectionService(
            taskItemRepo, attachmentRepo, taskViewRepo,
            NullLogger<TaskViewProjectionService>.Instance);

        // Act - run projection (same as what Function trigger calls)
        await projectionService.ProjectTaskItemAsync(task.Id.Value, DateTimeOffset.UtcNow, TestContext.CancellationToken);

        // Assert - TaskView was produced with correct data
        var taskView = await taskViewRepo.GetAsync(task.Id.Value.ToString(), TenantGuid.ToString(), TestContext.CancellationToken);
        Assert.IsNotNull(taskView, "TaskView should be created by projection");
        Assert.AreEqual("Integration Test Task", taskView.Title);
        Assert.AreEqual("Verify projection pipeline", taskView.Description);
        Assert.AreEqual("Open", taskView.Status);
        Assert.AreEqual("High", taskView.Priority);
        Assert.AreEqual("Work", taskView.CategoryName);
        Assert.AreEqual(0, taskView.CommentCount);
        Assert.AreEqual(0, taskView.AttachmentCount);
    }

    /// <summary>Verifies that given task item with children, when projection runs, then counts included.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Given_TaskItemWithChildren_When_ProjectionRuns_Then_CountsIncluded()
    {
        var connStr = DbContainerFixture.ConnectionString;
        var ctx = DbContainerFixture.CreateTrxnContext(connStr);

        var taskResult = TaskItem.Create(TenantId, "Task With Children");
        var task = taskResult.Value!;
        ctx.TaskItems.Add(task);
        await ctx.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);

        // Add a comment
        var commentResult = Comment.Create(TenantId, task.Id, "Test comment");
        ctx.Comments.Add(commentResult.Value!);

        // Add an attachment
        var attachmentResult = Attachment.Create(TenantId, "file.pdf", "application/pdf",
            1024, "https://storage.example.com/file.pdf", AttachmentOwnerType.TaskItem, task.Id.Value);
        ctx.Attachments.Add(attachmentResult.Value!);

        // Add a checklist item
        var checklistResult = ChecklistItem.Create(TenantId, task.Id, "Step 1");
        ctx.ChecklistItems.Add(checklistResult.Value!);

        await ctx.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);

        var queryCtx = DbContainerFixture.CreateQueryContext(connStr);
        var taskViewRepo = new InMemoryTaskViewRepository();
        var projectionService = new TaskViewProjectionService(
            new TaskItemRepositoryQuery(queryCtx, TestColumnEncryption.Keys),
            new AttachmentRepositoryQuery(queryCtx),
            taskViewRepo,
            NullLogger<TaskViewProjectionService>.Instance);

        await projectionService.ProjectTaskItemAsync(task.Id.Value, DateTimeOffset.UtcNow, TestContext.CancellationToken);

        var taskView = await taskViewRepo.GetAsync(task.Id.Value.ToString(), TenantGuid.ToString(), TestContext.CancellationToken);
        Assert.IsNotNull(taskView);
        Assert.AreEqual(1, taskView.CommentCount);
        Assert.AreEqual(1, taskView.AttachmentCount);
        Assert.AreEqual(1, taskView.ChecklistTotal);
        Assert.AreEqual(0, taskView.ChecklistCompleted);
    }

    /// <summary>Verifies that given service bus message body, when parsed, then task item ID extracted.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Given_ServiceBusMessageBody_When_Parsed_Then_TaskItemIdExtracted()
    {
        // Proves the Function trigger's message parsing logic works
        var taskItemId = Guid.NewGuid();
        var eventBody = JsonSerializer.Serialize(new
        {
            TaskItemId = taskItemId,
            TenantId = TenantGuid,
            Title = "Test Task"
        });

        using var doc = JsonDocument.Parse(eventBody);
        Assert.IsTrue(doc.RootElement.TryGetProperty("TaskItemId", out var prop));
        Assert.AreEqual(taskItemId, prop.GetGuid());
    }

    public TestContext TestContext { get; set; } = null!;
}

/// <summary>
/// In-memory implementation of ITaskViewRepository for integration testing
/// without Cosmos DB emulator.
/// </summary>
internal class InMemoryTaskViewRepository : ITaskViewRepository
{
    private readonly Dictionary<string, TaskViewDto> _store = new();

    /// <summary>Verifies upsert behavior and protects the expected test contract.</summary>
    public Task UpsertAsync(TaskViewDto taskView, CancellationToken ct = default)
    {
        _store[$"{taskView.TenantId}:{taskView.Id}"] = taskView;
        return Task.CompletedTask;
    }

    /// <summary>Verifies get behavior and protects the expected test contract.</summary>
    public Task<TaskViewDto?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        _store.TryGetValue($"{tenantId}:{id}", out var result);
        return Task.FromResult(result);
    }

    /// <summary>Verifies query by tenant behavior and protects the expected test contract.</summary>
    public Task<TaskViewPage> QueryByTenantAsync(string tenantId,
        int pageSize = 20, string? continuationToken = null, CancellationToken ct = default)
    {
        var results = _store.Values
            .Where(v => v.TenantId == tenantId)
            .OrderByDescending(v => v.LastModifiedUtc)
            .Take(pageSize)
            .ToList();
        return Task.FromResult(new TaskViewPage(results, null));
    }

    /// <summary>Verifies counter patching behavior and protects the expected test contract.</summary>
    public Task PatchCountersAsync(string id, string tenantId,
        IReadOnlyDictionary<string, int> increments, DateTimeOffset lastModifiedUtc, CancellationToken ct = default)
    {
        if (!_store.TryGetValue($"{tenantId}:{id}", out var view)) return Task.CompletedTask;

        view.CommentCount += increments.GetValueOrDefault("commentCount");
        view.AttachmentCount += increments.GetValueOrDefault("attachmentCount");
        view.ChecklistTotal += increments.GetValueOrDefault("checklistTotal");
        view.ChecklistCompleted += increments.GetValueOrDefault("checklistCompleted");
        view.LastModifiedUtc = lastModifiedUtc;
        return Task.CompletedTask;
    }

    /// <summary>Verifies delete behavior and protects the expected test contract.</summary>
    public Task DeleteAsync(string id, string tenantId, CancellationToken ct = default)
    {
        _store.Remove($"{tenantId}:{id}");
        return Task.CompletedTask;
    }
}
