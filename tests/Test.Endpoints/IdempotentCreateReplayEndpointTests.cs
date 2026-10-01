using EF.Testing.Http;
using System.Net;
using System.Net.Http.Json;
using TaskFlow.Application.Models;
using Test.Support;

namespace Test.Endpoints;

/// <summary>
/// D-033: a create resent with the same caller id replays only when the resend is the request the create already
/// applied. The replay compares the stored row with the request as the create would have applied it, so a field the
/// create defaults or discards cannot turn an identical resend into a 409, while a value the create would have stored
/// differently still does. Run under both application styles.
/// </summary>
[TestClass]
public class IdempotentCreateReplayEndpointTests
{
    private static EndpointStyleFixture _fixture = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A category create always stores <c>IsActive = true</c>, so a resend that omits <c>isActive</c> (false on the
    /// wire) replays; a resend with another sort order is a 409.
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_CategoryResentWithoutIsActive_When_Created_Then_ReplaysAndADifferentValueConflicts(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var id = Guid.CreateVersion7();
        var name = $"Replay-{Guid.NewGuid():N}"[..20];

        using var first = await PostAsync(client, "/api/v1/categories", new CategoryDto { Id = id, Name = name });
        using var resend = await PostAsync(client, "/api/v1/categories", new CategoryDto { Id = id, Name = name });
        using var different = await PostAsync(client, "/api/v1/categories", new CategoryDto { Id = id, Name = name, SortOrder = 5 });

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, resend.StatusCode, await resend.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(id, (await resend.ItemAsync<CategoryDto>(TestContext.CancellationToken))!.Id);
        Assert.AreEqual(HttpStatusCode.Conflict, different.StatusCode, await different.Content.ReadAsStringAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// A task create attaches a recurrence pattern only when both the interval and the frequency are present, and
    /// otherwise drops all three recurrence fields. A resend of a request that carried only an interval replays; a
    /// resend whose interval and frequency would have attached a pattern is a 409.
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_TaskItemResentWithADiscardedRecurrenceField_When_Created_Then_ReplaysAndADifferentValueConflicts(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var id = Guid.CreateVersion7();
        var title = $"Replay-{Guid.NewGuid():N}";

        using var first = await PostAsync(client, "/api/v1/task-items", new TaskItemDto { Id = id, Title = title, RecurrenceInterval = 2 });
        using var resend = await PostAsync(client, "/api/v1/task-items", new TaskItemDto { Id = id, Title = title, RecurrenceInterval = 2 });
        using var different = await PostAsync(client, "/api/v1/task-items",
            new TaskItemDto { Id = id, Title = title, RecurrenceInterval = 2, RecurrenceFrequency = "Daily" });

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, resend.StatusCode, await resend.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(id, (await resend.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Id);
        Assert.AreEqual(HttpStatusCode.Conflict, different.StatusCode, await different.Content.ReadAsStringAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// The database stores effort to two decimals and a date to whole microseconds, so the create normalizes them in the
    /// domain. An identical resend carrying more precision than that replays, and the stored value is the normalized one.
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_TaskItemResentWithMoreThanStoredPrecision_When_Created_Then_ReplaysWithTheStoredValues(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var id = Guid.CreateVersion7();
        var due = new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero).AddTicks(1234567);
        var dto = new TaskItemDto { Id = id, Title = $"Replay-{Guid.NewGuid():N}", EstimatedEffort = 1.23456m, ActualEffort = 2.005m, StartDate = due, DueDate = due };

        using var first = await PostAsync(client, "/api/v1/task-items", dto);
        using var resend = await PostAsync(client, "/api/v1/task-items", dto);

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, resend.StatusCode, await resend.Content.ReadAsStringAsync(TestContext.CancellationToken));
        var stored = (await resend.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!;
        Assert.AreEqual(1.23m, stored.EstimatedEffort);
        Assert.AreEqual(2.01m, stored.ActualEffort);
        Assert.AreEqual(due.Ticks - 7, stored.DueDate!.Value.Ticks);
        Assert.AreEqual(due.Ticks - 7, stored.StartDate!.Value.Ticks);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string url, T item) =>
        await client.PostAsJsonAsync(url, new DefaultRequest<T> { Item = item }, cancellationToken: TestContext.CancellationToken);
}
