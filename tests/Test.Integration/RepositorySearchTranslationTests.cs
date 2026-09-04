using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;
using Test.Support.Builders;

namespace Test.Integration;

/// <summary>
/// Exercises repository search predicates against real SQL so translated filters over tenant IDs,
/// nullable FKs, enums, owned values, and projected DTOs cannot regress behind in-memory endpoint tests.
/// Each case asserts the returned page AND <c>Total</c>: a positional <c>QueryPageProjectionAsync</c>
/// call (swapped pageSize/pageIndex, or includeTotal:false yielding Total = -1) is invisible to the
/// fake providers used in fast tiers and only surfaces here against real SQL.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class RepositorySearchTranslationTests
{
    private static readonly Guid TenantId = TestConstants.TenantId;

    /// <summary>Ensures the shared SQL schema exists before repository search translation tests run.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        if (IntegrationTestSetup.IsUnavailable(SqlContainerFixture.StartupError))
            return;

        await using var db = SqlContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(_.CancellationToken);
    }

    /// <summary>Marks the test Inconclusive when the SQL container failed to start.</summary>
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.AssertAvailable("SQL", SqlContainerFixture.StartupError);
    }

    /// <summary>Verifies category search translates tenant, parent, bool, and string filters against SQL.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task CategorySearch_FiltersByTenantParentAndName_AgainstRealSql()
    {
        var marker = $"SearchCategory-{Guid.NewGuid():N}";

        await using (var db = SqlContainerFixture.CreateTrxnContext())
        {
            var parent = new CategoryBuilder().WithTenantId(TenantId).WithName($"{marker}-Parent").Build();
            var child = new CategoryBuilder()
                .WithTenantId(TenantId)
                .WithName($"{marker}-Child")
                .WithParentCategoryId(parent.Id)
                .Build();

            db.Categories.AddRange(parent, child);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);

            await using var queryDb = SqlContainerFixture.CreateQueryContext();
            var repo = new CategoryRepositoryQuery(queryDb);
            var page = await repo.SearchCategoriesAsync(new SearchRequest<CategorySearchFilter>
            {
                PageIndex = 1,
                PageSize = 10,
                Filter = new CategorySearchFilter
                {
                    SearchTerm = marker,
                    TenantId = TenantId,
                    ParentCategoryId = parent.Id,
                    IsActive = true
                }
            }, TestContext.CancellationToken);

            Assert.HasCount(1, page.Data);
            Assert.AreEqual(1, page.Total);
            Assert.AreEqual($"{marker}-Child", page.Data[0].Name);
        }
    }

    /// <summary>Verifies tag search translates tenant and string filters against SQL.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task TagSearch_FiltersByTenantAndName_AgainstRealSql()
    {
        var marker = $"SearchTag-{Guid.NewGuid():N}";

        await using (var db = SqlContainerFixture.CreateTrxnContext())
        {
            db.Tags.Add(new TagBuilder().WithTenantId(TenantId).WithName(marker).Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = SqlContainerFixture.CreateQueryContext();
        var repo = new TagRepositoryQuery(queryDb);
        var page = await repo.SearchTagsAsync(new SearchRequest<TagSearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new TagSearchFilter { SearchTerm = marker, TenantId = TenantId }
        }, TestContext.CancellationToken);

        Assert.HasCount(1, page.Data);
        Assert.AreEqual(1, page.Total);
        Assert.AreEqual(marker, page.Data[0].Name);
    }

    /// <summary>Verifies comment search translates tenant, task FK, and string filters against SQL.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task CommentSearch_FiltersByTenantTaskAndBody_AgainstRealSql()
    {
        var marker = $"SearchComment-{Guid.NewGuid():N}";
        Guid taskId;

        await using (var db = SqlContainerFixture.CreateTrxnContext())
        {
            var task = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Task").Build();
            db.TaskItems.Add(task);
            db.Comments.Add(new CommentBuilder().WithTenantId(TenantId).WithTaskItemId(task.Id).WithBody(marker).Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            taskId = task.Id;
        }

        await using var queryDb = SqlContainerFixture.CreateQueryContext();
        var repo = new CommentRepositoryQuery(queryDb);
        var page = await repo.SearchCommentsAsync(new SearchRequest<CommentSearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new CommentSearchFilter { SearchTerm = marker, TenantId = TenantId, TaskItemId = taskId }
        }, TestContext.CancellationToken);

        Assert.HasCount(1, page.Data);
        Assert.AreEqual(1, page.Total);
        Assert.AreEqual(marker, page.Data[0].Body);
    }

    /// <summary>Verifies checklist search translates tenant, task FK, bool, and string filters against SQL.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ChecklistItemSearch_FiltersByTenantTaskStatusAndTitle_AgainstRealSql()
    {
        var marker = $"SearchChecklist-{Guid.NewGuid():N}";
        Guid taskId;

        await using (var db = SqlContainerFixture.CreateTrxnContext())
        {
            var task = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Task").Build();
            db.TaskItems.Add(task);
            db.ChecklistItems.Add(new ChecklistItemBuilder()
                .WithTenantId(TenantId)
                .WithTaskItemId(task.Id)
                .WithTitle(marker)
                .Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            taskId = task.Id;
        }

        await using var queryDb = SqlContainerFixture.CreateQueryContext();
        var repo = new ChecklistItemRepositoryQuery(queryDb);
        var page = await repo.SearchChecklistItemsAsync(new SearchRequest<ChecklistItemSearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new ChecklistItemSearchFilter
            {
                SearchTerm = marker,
                TenantId = TenantId,
                TaskItemId = taskId,
                IsCompleted = false
            }
        }, TestContext.CancellationToken);

        Assert.HasCount(1, page.Data);
        Assert.AreEqual(1, page.Total);
        Assert.AreEqual(marker, page.Data[0].Title);
    }

    /// <summary>Verifies task search translates typed IDs, enums, owned date range filters, and projection against SQL.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task TaskItemSearch_FiltersByTypedIdsEnumsDatesAndTitle_AgainstRealSql()
    {
        var marker = $"SearchTask-{Guid.NewGuid():N}";
        var dueDate = DateTimeOffset.UtcNow.AddDays(3);
        Guid categoryId;
        Guid parentTaskItemId;

        await using (var db = SqlContainerFixture.CreateTrxnContext())
        {
            var category = new CategoryBuilder().WithTenantId(TenantId).WithName($"{marker}-Category").Build();
            var parent = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Parent").Build();
            var child = new TaskItemBuilder()
                .WithTenantId(TenantId)
                .WithTitle($"{marker}-Child")
                .WithPriority(Priority.High)
                .WithCategoryId(category.Id)
                .WithParentTaskItemId(parent.Id)
                .Build();
            child.UpdateDateRange(dueDate.AddDays(-1), dueDate);

            db.Categories.Add(category);
            db.TaskItems.AddRange(parent, child);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);

            categoryId = category.Id;
            parentTaskItemId = parent.Id;
        }

        await using var queryDb = SqlContainerFixture.CreateQueryContext();
        var repo = new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys);
        var page = await repo.SearchTaskItemsAsync(new SearchRequest<TaskItemSearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new TaskItemSearchFilter
            {
                SearchTerm = marker,
                TenantId = TenantId,
                Status = TaskItemStatus.Open,
                Priority = Priority.High,
                CategoryId = categoryId,
                ParentTaskItemId = parentTaskItemId,
                DueAfter = dueDate.AddDays(-2),
                DueBefore = dueDate.AddDays(1)
            }
        }, TestContext.CancellationToken);

        Assert.HasCount(1, page.Data);
        Assert.AreEqual(1, page.Total);
        Assert.AreEqual($"{marker}-Child", page.Data[0].Title);
        Assert.AreEqual(categoryId, page.Data[0].CategoryId);
        Assert.AreEqual(dueDate, page.Data[0].DueDate);
    }

    /// <summary>Verifies attachment search translates tenant, enum, owner ID, and string filters against SQL.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task AttachmentSearch_FiltersByTenantOwnerAndFileName_AgainstRealSql()
    {
        var marker = $"SearchAttachment-{Guid.NewGuid():N}";
        var ownerId = Guid.NewGuid();

        await using (var db = SqlContainerFixture.CreateTrxnContext())
        {
            db.Attachments.Add(new AttachmentBuilder()
                .WithTenantId(TenantId)
                .WithFileName($"{marker}.txt")
                .WithOwnerType(AttachmentOwnerType.TaskItem)
                .WithOwnerId(ownerId)
                .Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = SqlContainerFixture.CreateQueryContext();
        var repo = new AttachmentRepositoryQuery(queryDb);
        var page = await repo.SearchAttachmentsAsync(new SearchRequest<AttachmentSearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new AttachmentSearchFilter
            {
                SearchTerm = marker,
                TenantId = TenantId,
                OwnerType = AttachmentOwnerType.TaskItem,
                OwnerId = ownerId
            }
        }, TestContext.CancellationToken);

        Assert.HasCount(1, page.Data);
        Assert.AreEqual(1, page.Total);
        Assert.AreEqual($"{marker}.txt", page.Data[0].FileName);
    }

    public TestContext TestContext { get; set; } = null!;
}
