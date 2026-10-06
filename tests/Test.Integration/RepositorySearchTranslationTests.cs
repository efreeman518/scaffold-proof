using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
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
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError))
            return;

        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(_.CancellationToken);
    }

    /// <summary>Inconclusive without a container runtime; fails when the SQL container failed to start.</summary>
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);
    }

    /// <summary>Verifies category search translates tenant, parent, bool, and string filters against SQL.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task CategorySearch_FiltersByTenantParentAndName_AgainstRealSql()
    {
        var marker = $"SearchCategory-{Guid.NewGuid():N}";

        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            var parent = new CategoryBuilder().WithTenantId(TenantId).WithName($"{marker}-Parent").Build();
            var child = new CategoryBuilder()
                .WithTenantId(TenantId)
                .WithName($"{marker}-Child")
                .WithParentCategoryId(parent.Id)
                .Build();

            db.Categories.AddRange(parent, child);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);

            await using var queryDb = DbContainerFixture.CreateQueryContext();
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
            }, includeTotal: true, TestContext.CancellationToken);

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

        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            db.Tags.Add(new TagBuilder().WithTenantId(TenantId).WithName(marker).Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var repo = new TagRepositoryQuery(queryDb);
        var page = await repo.SearchTagsAsync(new SearchRequest<TagSearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new TagSearchFilter { SearchTerm = marker, TenantId = TenantId }
        }, includeTotal: true, TestContext.CancellationToken);

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

        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            var task = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Task").Build();
            db.TaskItems.Add(task);
            db.Comments.Add(new CommentBuilder().WithTenantId(TenantId).WithTaskItemId(task.Id).WithBody(marker).Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            taskId = task.Id;
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var repo = new CommentRepositoryQuery(queryDb);
        var page = await repo.SearchCommentsAsync(new SearchRequest<CommentSearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new CommentSearchFilter { SearchTerm = marker, TenantId = TenantId, TaskItemId = taskId }
        }, includeTotal: true, TestContext.CancellationToken);

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

        await using (var db = DbContainerFixture.CreateTrxnContext())
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

        await using var queryDb = DbContainerFixture.CreateQueryContext();
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
        }, includeTotal: true, TestContext.CancellationToken);

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
        // Whole seconds: SQL Server keeps 100ns ticks, PostgreSQL timestamptz keeps microseconds, so an
        // unaligned UtcNow would not round-trip equal on both providers.
        var dueDate = new DateTimeOffset(2026, 12, 1, 9, 30, 0, TimeSpan.Zero);
        Guid categoryId;
        Guid parentTaskItemId;

        await using (var db = DbContainerFixture.CreateTrxnContext())
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

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var repo = new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys, TestCursorCodec.Instance);
        var page = await repo.SearchTaskItemsAsync(new TaskItemCursorSearchRequest
        {
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
        }, TenantId, TestContext.CancellationToken);

        Assert.HasCount(1, page.Items);
        Assert.IsFalse(page.HasMore);
        Assert.AreEqual($"{marker}-Child", page.Items[0].Title);
        Assert.AreEqual(categoryId, page.Items[0].CategoryId);
        Assert.AreEqual(dueDate, page.Items[0].DueDate);
    }

    /// <summary>
    /// The tag name filter translates to an EXISTS over TaskItemTag and Tag on both providers and matches the trimmed name
    /// case-insensitively (PostgreSQL's default collation is case-sensitive, SQL Server's is not): only the task carrying
    /// the tag is returned, not an untagged task or one carrying another tag.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task TaskItemSearch_FiltersByTagNameCaseInsensitively_AgainstRealSql()
    {
        var marker = $"SearchTagged-{Guid.NewGuid():N}";
        var tagName = $"Compliance-{marker[^12..]}";

        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            var tag = new TagBuilder().WithTenantId(TenantId).WithName(tagName).Build();
            var otherTag = new TagBuilder().WithTenantId(TenantId).WithName($"Other-{marker[^12..]}").Build();
            var tagged = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Tagged").Build();
            var otherTagged = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-OtherTagged").Build();
            var untagged = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Untagged").Build();
            tagged.AssociateTag(tag.Id);
            otherTagged.AssociateTag(otherTag.Id);

            db.Tags.AddRange(tag, otherTag);
            db.TaskItems.AddRange(tagged, otherTagged, untagged);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var repo = new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys, TestCursorCodec.Instance);
        var page = await repo.SearchTaskItemsAsync(new TaskItemCursorSearchRequest
        {
            PageSize = 10,
            Filter = new TaskItemSearchFilter { SearchTerm = marker, TenantId = TenantId, TagName = $" {tagName.ToLowerInvariant()} " }
        }, TenantId, TestContext.CancellationToken);

        Assert.HasCount(1, page.Items);
        Assert.AreEqual($"{marker}-Tagged", page.Items[0].Title);
    }

    /// <summary>
    /// D-024: a caller-supplied DueBefore with a non-zero offset must translate on both providers (Npgsql rejects
    /// non-UTC DateTimeOffset parameters unless the UTC converter normalizes them) and compare by instant.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task TaskItemSearch_DueBeforeWithNonUtcOffset_ComparesByInstant()
    {
        var marker = $"SearchOffset-{Guid.NewGuid():N}";
        var dueUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            var task = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Due").Build();
            task.UpdateDateRange(null, dueUtc);
            db.TaskItems.Add(task);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var repo = new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys, TestCursorCodec.Instance);

        // 18:00 +05:00 is 13:00Z: one hour after the due instant, so the task is due before it.
        var matching = await repo.SearchTaskItemsAsync(new TaskItemCursorSearchRequest
        {
            PageSize = 10,
            Filter = new TaskItemSearchFilter { SearchTerm = marker, TenantId = TenantId, DueBefore = new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.FromHours(5)) }
        }, TenantId, TestContext.CancellationToken);
        // 16:00 +05:00 is 11:00Z: one hour before the due instant, so nothing matches.
        var none = await repo.SearchTaskItemsAsync(new TaskItemCursorSearchRequest
        {
            PageSize = 10,
            Filter = new TaskItemSearchFilter { SearchTerm = marker, TenantId = TenantId, DueBefore = new DateTimeOffset(2026, 6, 1, 16, 0, 0, TimeSpan.FromHours(5)) }
        }, TenantId, TestContext.CancellationToken);

        Assert.HasCount(1, matching.Items);
        Assert.AreEqual(dueUtc, matching.Items[0].DueDate);
        Assert.IsEmpty(none.Items);
    }

    /// <summary>Verifies attachment search translates tenant, enum, owner ID, and string filters against SQL.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task AttachmentSearch_FiltersByTenantOwnerAndFileName_AgainstRealSql()
    {
        var marker = $"SearchAttachment-{Guid.NewGuid():N}";
        var ownerId = Guid.NewGuid();

        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            db.Attachments.Add(new AttachmentBuilder()
                .WithTenantId(TenantId)
                .WithFileName($"{marker}.txt")
                .WithOwnerType(AttachmentOwnerType.TaskItem)
                .WithOwnerId(ownerId)
                .Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
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
        }, includeTotal: true, TestContext.CancellationToken);

        Assert.HasCount(1, page.Data);
        Assert.AreEqual(1, page.Total);
        Assert.AreEqual($"{marker}.txt", page.Data[0].FileName);
    }

    /// <summary>
    /// The content type filter translates on both providers and compares media types only: case-insensitive, parameters
    /// such as charset ignored, so "text/plain; charset=utf-8" and "TEXT/MARKDOWN" match and "application/pdf" does not.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task AttachmentSearch_FiltersByMediaTypeIgnoringCaseAndParameters_AgainstRealSql()
    {
        var ownerId = Guid.NewGuid();
        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            db.Attachments.AddRange(
                new AttachmentBuilder().WithTenantId(TenantId).WithOwnerId(ownerId).WithFileName("a.txt").WithContentType("text/plain; charset=utf-8").Build(),
                new AttachmentBuilder().WithTenantId(TenantId).WithOwnerId(ownerId).WithFileName("b.md").WithContentType("TEXT/MARKDOWN").Build(),
                new AttachmentBuilder().WithTenantId(TenantId).WithOwnerId(ownerId).WithFileName("c.pdf").WithContentType("application/pdf").Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var page = await new AttachmentRepositoryQuery(queryDb).SearchAttachmentsAsync(new SearchRequest<AttachmentSearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new AttachmentSearchFilter { TenantId = TenantId, OwnerId = ownerId, ContentTypes = ["Text/Plain", "text/markdown; charset=utf-8"] }
        }, includeTotal: true, TestContext.CancellationToken);

        Assert.AreEqual(2, page.Total);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "b.md" }, page.Data.Select(a => a.FileName).ToArray());
    }

    /// <summary>
    /// The id tie-break follows the sort direction: for rows stamped with the same CreatedAtUtc (one save), a descending
    /// sort returns the row whose id is greatest in the provider's own uniqueidentifier/uuid ordering.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task AttachmentSearch_SortDirection_AlsoOrdersTheIdTieBreak_AgainstRealSql()
    {
        var ownerId = Guid.NewGuid();
        var first = new AttachmentBuilder().WithTenantId(TenantId).WithOwnerId(ownerId).WithFileName("first.txt").Build();
        var second = new AttachmentBuilder().WithTenantId(TenantId).WithOwnerId(ownerId).WithFileName("second.txt").Build();
        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            db.Attachments.AddRange(first, second);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        Assert.AreEqual(first.CreatedAtUtc, second.CreatedAtUtc, "precondition: one save stamps one CreatedAtUtc");
        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var ownerFilter = queryDb.Attachments.IgnoreQueryFilters().Where(a => a.OwnerId == ownerId);
        var greatestId = await ownerFilter.OrderByDescending(a => a.Id).Select(a => a.FileName).FirstAsync(TestContext.CancellationToken);
        var leastId = await ownerFilter.OrderBy(a => a.Id).Select(a => a.FileName).FirstAsync(TestContext.CancellationToken);
        var page = await new AttachmentRepositoryQuery(queryDb).SearchAttachmentsAsync(new SearchRequest<AttachmentSearchFilter>
        {
            PageIndex = 1,
            PageSize = 1,
            Sorts = [new Sort("CreatedAtUtc", SortOrder.Descending)],
            Filter = new AttachmentSearchFilter { TenantId = TenantId, OwnerId = ownerId }
        }, includeTotal: false, TestContext.CancellationToken);
        var ascending = await new AttachmentRepositoryQuery(queryDb).SearchAttachmentsAsync(new SearchRequest<AttachmentSearchFilter>
        {
            PageIndex = 1,
            PageSize = 1,
            Sorts = [new Sort("CreatedAtUtc", SortOrder.Ascending)],
            Filter = new AttachmentSearchFilter { TenantId = TenantId, OwnerId = ownerId }
        }, includeTotal: false, TestContext.CancellationToken);

        Assert.AreEqual(greatestId, page.Data.Single().FileName, "a descending sort breaks the tie by descending id");
        Assert.AreEqual(leastId, ascending.Data.Single().FileName, "an ascending sort breaks the tie by ascending id");
    }

    /// <summary>
    /// Verifies a sort direction also orders the id tie-break: two categories tied on the sort key come back in the
    /// provider's ascending id order for an ascending sort and in descending id order for a descending one.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task CategorySearch_SortDirection_AlsoOrdersTheIdTieBreak_AgainstRealSql()
    {
        var marker = $"TieCategory-{Guid.NewGuid():N}";
        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            db.Categories.AddRange(
                new CategoryBuilder().WithTenantId(TenantId).WithName($"{marker}-a").WithSortOrder(7).Build(),
                new CategoryBuilder().WithTenantId(TenantId).WithName($"{marker}-b").WithSortOrder(7).Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var ascendingNames = await queryDb.Categories.IgnoreQueryFilters().Where(e => e.Name.StartsWith(marker))
            .OrderBy(e => e.Id).Select(e => e.Name).ToListAsync(TestContext.CancellationToken);
        Assert.HasCount(2, ascendingNames);
        var repo = new CategoryRepositoryQuery(queryDb);
        foreach (var order in new[] { SortOrder.Ascending, SortOrder.Descending })
        {
            var page = await repo.SearchCategoriesAsync(new SearchRequest<CategorySearchFilter>
            {
                PageIndex = 1,
                PageSize = 10,
                Sorts = [new Sort("SortOrder", order)],
                Filter = new CategorySearchFilter { SearchTerm = marker, TenantId = TenantId }
            }, includeTotal: false, TestContext.CancellationToken);
            CollectionAssert.AreEqual(ExpectedTieOrder(ascendingNames, order), page.Data.Select(e => e.Name).ToList(), $"{order}");
        }
    }

    /// <summary>Verifies tag search breaks a sort-key tie in the sort direction.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task TagSearch_SortDirection_AlsoOrdersTheIdTieBreak_AgainstRealSql()
    {
        var marker = $"TieTag-{Guid.NewGuid():N}";
        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            db.Tags.AddRange(
                new TagBuilder().WithTenantId(TenantId).WithName($"{marker}-a").WithColor("#112233").Build(),
                new TagBuilder().WithTenantId(TenantId).WithName($"{marker}-b").WithColor("#112233").Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var ascendingNames = await queryDb.Tags.IgnoreQueryFilters().Where(e => e.Name.StartsWith(marker))
            .OrderBy(e => e.Id).Select(e => e.Name).ToListAsync(TestContext.CancellationToken);
        Assert.HasCount(2, ascendingNames);
        var repo = new TagRepositoryQuery(queryDb);
        foreach (var order in new[] { SortOrder.Ascending, SortOrder.Descending })
        {
            var page = await repo.SearchTagsAsync(new SearchRequest<TagSearchFilter>
            {
                PageIndex = 1,
                PageSize = 10,
                Sorts = [new Sort("Color", order)],
                Filter = new TagSearchFilter { SearchTerm = marker, TenantId = TenantId }
            }, includeTotal: false, TestContext.CancellationToken);
            CollectionAssert.AreEqual(ExpectedTieOrder(ascendingNames, order), page.Data.Select(e => e.Name).ToList(), $"{order}");
        }
    }

    /// <summary>Verifies comment search breaks a sort-key tie in the sort direction.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task CommentSearch_SortDirection_AlsoOrdersTheIdTieBreak_AgainstRealSql()
    {
        var marker = $"TieComment-{Guid.NewGuid():N}";
        Guid taskId;
        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            var task = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Task").Build();
            var first = new CommentBuilder().WithTenantId(TenantId).WithTaskItemId(task.Id).WithBody($"{marker}-a").Build();
            var second = new CommentBuilder().WithTenantId(TenantId).WithTaskItemId(task.Id).WithBody($"{marker}-b").Build();
            db.TaskItems.Add(task);
            db.Comments.AddRange(first, second);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(first.CreatedAtUtc, second.CreatedAtUtc, "precondition: one save stamps one CreatedAtUtc");
            taskId = task.Id;
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var ascendingBodies = await queryDb.Comments.IgnoreQueryFilters().Where(e => e.Body.StartsWith(marker))
            .OrderBy(e => e.Id).Select(e => e.Body).ToListAsync(TestContext.CancellationToken);
        Assert.HasCount(2, ascendingBodies);
        var repo = new CommentRepositoryQuery(queryDb);
        foreach (var order in new[] { SortOrder.Ascending, SortOrder.Descending })
        {
            var page = await repo.SearchCommentsAsync(new SearchRequest<CommentSearchFilter>
            {
                PageIndex = 1,
                PageSize = 10,
                Sorts = [new Sort("CreatedAtUtc", order)],
                Filter = new CommentSearchFilter { SearchTerm = marker, TenantId = TenantId, TaskItemId = taskId }
            }, includeTotal: false, TestContext.CancellationToken);
            CollectionAssert.AreEqual(ExpectedTieOrder(ascendingBodies, order), page.Data.Select(e => e.Body).ToList(), $"{order}");
        }
    }

    /// <summary>Verifies checklist item search breaks a sort-key tie in the sort direction.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ChecklistItemSearch_SortDirection_AlsoOrdersTheIdTieBreak_AgainstRealSql()
    {
        var marker = $"TieChecklist-{Guid.NewGuid():N}";
        Guid taskId;
        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            var task = new TaskItemBuilder().WithTenantId(TenantId).WithTitle($"{marker}-Task").Build();
            db.TaskItems.Add(task);
            db.ChecklistItems.AddRange(
                new ChecklistItemBuilder().WithTenantId(TenantId).WithTaskItemId(task.Id).WithTitle($"{marker}-a").WithSortOrder(3).Build(),
                new ChecklistItemBuilder().WithTenantId(TenantId).WithTaskItemId(task.Id).WithTitle($"{marker}-b").WithSortOrder(3).Build());
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            taskId = task.Id;
        }

        await using var queryDb = DbContainerFixture.CreateQueryContext();
        var ascendingTitles = await queryDb.ChecklistItems.IgnoreQueryFilters().Where(e => e.Title.StartsWith(marker))
            .OrderBy(e => e.Id).Select(e => e.Title).ToListAsync(TestContext.CancellationToken);
        Assert.HasCount(2, ascendingTitles);
        var repo = new ChecklistItemRepositoryQuery(queryDb);
        foreach (var order in new[] { SortOrder.Ascending, SortOrder.Descending })
        {
            var page = await repo.SearchChecklistItemsAsync(new SearchRequest<ChecklistItemSearchFilter>
            {
                PageIndex = 1,
                PageSize = 10,
                Sorts = [new Sort("SortOrder", order)],
                Filter = new ChecklistItemSearchFilter { SearchTerm = marker, TenantId = TenantId, TaskItemId = taskId }
            }, includeTotal: false, TestContext.CancellationToken);
            CollectionAssert.AreEqual(ExpectedTieOrder(ascendingTitles, order), page.Data.Select(e => e.Title).ToList(), $"{order}");
        }
    }

    private static List<string> ExpectedTieOrder(List<string> ascendingById, SortOrder order) =>
        order == SortOrder.Descending ? [.. ascendingById.AsEnumerable().Reverse()] : ascendingById;

    public TestContext TestContext { get; set; } = null!;
}
