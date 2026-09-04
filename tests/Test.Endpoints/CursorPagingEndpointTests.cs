using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Application.Contracts.Paging;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Endpoints;

/// <summary>
/// Keyset paging contract for <c>/task-items/search</c> (GR-18), under both application styles: page
/// size limits, a complete page-through with no duplicate or missing row, and the three ways a cursor
/// can be wrong - tampered, foreign tenant, wrong sort mode - each of which is a 400 rather than a
/// silent restart at page one.
/// </summary>
[TestClass]
public class CursorPagingEndpointTests
{
    private static EndpointStyleFixture _fixture = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    /// <summary>Seeds tasks that share a unique title marker so a page-through is isolated from other tests.</summary>
    private async Task<string> SeedAsync(HttpClient client, int count)
    {
        var marker = $"Cursor-{Guid.NewGuid():N}";
        for (var i = 0; i < count; i++)
        {
            var dto = new TaskItemDto
            {
                Title = $"{marker}-{i:D2}",
                Priority = Priority.Medium,
                DueDate = i % 3 == 0 ? null : DateTimeOffset.UtcNow.AddDays(i)
            };
            using var response = await client.PostAsJsonAsync("/api/v1/task-items",
                new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        }
        return marker;
    }

    /// <summary>Posts one search page and returns the parsed document.</summary>
    private async Task<(HttpStatusCode Status, JsonDocument? Document)> SearchAsync(
        HttpClient client, string marker, TaskItemSortMode sortMode, int pageSize, string? cursor)
    {
        var request = new TaskItemCursorSearchRequest
        {
            Filter = new TaskItemSearchFilter { SearchTerm = marker },
            SortMode = sortMode,
            PageSize = pageSize,
            Cursor = cursor
        };

        using var response = await client.PostAsJsonAsync("/api/v1/task-items/search", request,
            cancellationToken: TestContext.CancellationToken);

        if (!response.IsSuccessStatusCode) return (response.StatusCode, null);

        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(body));
    }

    /// <summary>Verifies a full page-through returns every seeded row exactly once and ends with HasMore false.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service, (int)TaskItemSortMode.IdAsc)]
    [DataRow(EndpointStyles.Service, (int)TaskItemSortMode.DueDateAsc)]
    [DataRow(EndpointStyles.Service, (int)TaskItemSortMode.DueDateDesc)]
    [DataRow(EndpointStyles.Service, (int)TaskItemSortMode.ModifiedDesc)]
    [DataRow(EndpointStyles.Service, (int)TaskItemSortMode.StatusThenId)]
    [DataRow(EndpointStyles.Cqrs, (int)TaskItemSortMode.IdAsc)]
    [DataRow(EndpointStyles.Cqrs, (int)TaskItemSortMode.DueDateAsc)]
    [DataRow(EndpointStyles.Cqrs, (int)TaskItemSortMode.DueDateDesc)]
    [DataRow(EndpointStyles.Cqrs, (int)TaskItemSortMode.ModifiedDesc)]
    [DataRow(EndpointStyles.Cqrs, (int)TaskItemSortMode.StatusThenId)]
    [TestMethod]
    public async Task Given_SeededTasks_When_PagedThrough_Then_NoDuplicatesOrGaps(string style, int sortMode)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var marker = await SeedAsync(client, 7);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;

        while (pages++ < 10)
        {
            var (status, document) = await SearchAsync(client, marker, (TaskItemSortMode)sortMode, 3, cursor);
            Assert.AreEqual(HttpStatusCode.OK, status);
            using var doc = document!;

            seen.AddRange(doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(e => e.GetProperty("id").GetGuid()));

            if (!doc.RootElement.GetProperty("hasMore").GetBoolean())
            {
                Assert.IsTrue(
                    doc.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null,
                    "The final page must not hand out a cursor: there is nothing after it.");
                break;
            }

            cursor = doc.RootElement.GetProperty("nextCursor").GetString();
            Assert.IsNotNull(cursor, "A page with HasMore must carry the cursor for the next one.");
        }

        Assert.HasCount(7, seen);
        Assert.AreEqual(7, seen.Distinct().Count(), "Keyset paging must not repeat a row across pages.");
    }

    /// <summary>Verifies each sort mode returns rows in its declared order.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SortModes_When_Searched_Then_RowsAreOrdered(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var marker = await SeedAsync(client, 6);

        var (_, byDueAsc) = await SearchAsync(client, marker, TaskItemSortMode.DueDateAsc, 50, null);
        using var asc = byDueAsc!;
        var ascDates = asc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.TryGetProperty("dueDate", out var d) && d.ValueKind != JsonValueKind.Null
                ? d.GetDateTimeOffset()
                : (DateTimeOffset?)null)
            .ToList();

        // Nulls sort last, and the non-null prefix is ascending.
        var firstNull = ascDates.FindIndex(d => d is null);
        if (firstNull >= 0)
            Assert.IsTrue(ascDates.Skip(firstNull).All(d => d is null), "Null due dates must sort last.");
        var nonNullAsc = ascDates.Where(d => d is not null).Select(d => d!.Value).ToList();
        CollectionAssert.AreEqual(nonNullAsc.OrderBy(d => d).ToList(), nonNullAsc);

        var (_, byDueDesc) = await SearchAsync(client, marker, TaskItemSortMode.DueDateDesc, 50, null);
        using var desc = byDueDesc!;
        var descDates = desc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.TryGetProperty("dueDate", out var d) && d.ValueKind != JsonValueKind.Null
                ? d.GetDateTimeOffset()
                : (DateTimeOffset?)null)
            .Where(d => d is not null).Select(d => d!.Value).ToList();
        CollectionAssert.AreEqual(descDates.OrderByDescending(d => d).ToList(), descDates);
    }

    /// <summary>Verifies a page size below the floor is refused rather than clamped.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_PageSizeZero_When_Search_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        var (status, _) = await SearchAsync(client, "anything", TaskItemSortMode.IdAsc, 0, null);

        Assert.AreEqual(HttpStatusCode.BadRequest, status);
    }

    /// <summary>Verifies a page size above the ceiling is refused rather than clamped.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_PageSizeAboveMax_When_Search_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        var (status, _) = await SearchAsync(client, "anything", TaskItemSortMode.IdAsc, PageSizeLimits.Max + 1, null);

        Assert.AreEqual(HttpStatusCode.BadRequest, status);
    }

    /// <summary>Verifies an offset-entity search rejects an out-of-range page size too.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_PageSizeAboveMax_When_CategorySearch_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        using var response = await client.PostAsJsonAsync("/api/v1/categories/search",
            new EF.Common.Contracts.SearchRequest<CategorySearchFilter> { PageIndex = 1, PageSize = 500 },
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Verifies totals are opt-in: absent by default, present with includeTotal=true.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_CategorySearch_When_IncludeTotalToggled_Then_TotalIsOptIn(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        var name = $"Total-{Guid.NewGuid():N}";
        using var create = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Name = name, IsActive = true } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);

        var request = new EF.Common.Contracts.SearchRequest<CategorySearchFilter>
        {
            PageIndex = 1,
            PageSize = 10,
            Filter = new CategorySearchFilter { SearchTerm = name }
        };

        using var without = await client.PostAsJsonAsync("/api/v1/categories/search", request,
            cancellationToken: TestContext.CancellationToken);
        using var withoutDoc = JsonDocument.Parse(await without.Content.ReadAsStringAsync(TestContext.CancellationToken));
        // The package returns -1 for "not computed"; the point is that it is not the real count.
        Assert.AreEqual(-1, withoutDoc.RootElement.GetProperty("total").GetInt32(),
            "Totals cost a second query, so they are not computed unless asked for.");

        using var with = await client.PostAsJsonAsync("/api/v1/categories/search?includeTotal=true", request,
            cancellationToken: TestContext.CancellationToken);
        using var withDoc = JsonDocument.Parse(await with.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(1, withDoc.RootElement.GetProperty("total").GetInt32());
    }

    /// <summary>Verifies a tampered cursor is a 400, not a silent restart at page one.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_TamperedCursor_When_Search_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var marker = await SeedAsync(client, 4);

        var (_, first) = await SearchAsync(client, marker, TaskItemSortMode.IdAsc, 2, null);
        using var page = first!;
        var cursor = page.RootElement.GetProperty("nextCursor").GetString()!;

        // Flip one character of the protected payload.
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? 'B' : 'A') + cursor[^1];

        var (status, _) = await SearchAsync(client, marker, TaskItemSortMode.IdAsc, 2, tampered);

        Assert.AreEqual(HttpStatusCode.BadRequest, status);
    }

    /// <summary>Verifies a cursor minted for another tenant is refused (it must never leak rows).</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_CrossTenantCursor_When_Search_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        // Minted with the app's own protector, so it is cryptographically valid - only the tenant is wrong.
        var protector = _fixture.Factory(style).Services.GetRequiredService<ICursorProtector>();
        var foreign = protector.Protect(new CursorToken(
            TaskItemSortMode.IdAsc, Guid.NewGuid(), string.Empty, Guid.CreateVersion7()));

        var (status, _) = await SearchAsync(client, "anything", TaskItemSortMode.IdAsc, 2, foreign);

        Assert.AreEqual(HttpStatusCode.BadRequest, status);
    }

    /// <summary>Verifies a cursor from a different sort mode is refused rather than reinterpreted.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SortModeMismatch_When_Search_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var marker = await SeedAsync(client, 4);

        var (_, first) = await SearchAsync(client, marker, TaskItemSortMode.IdAsc, 2, null);
        using var page = first!;
        var cursor = page.RootElement.GetProperty("nextCursor").GetString();

        // Same cursor, different ordering: the position it names means nothing in the new order.
        var (status, _) = await SearchAsync(client, marker, TaskItemSortMode.DueDateAsc, 2, cursor);

        Assert.AreEqual(HttpStatusCode.BadRequest, status);
    }

    public TestContext TestContext { get; set; } = null!;
}
