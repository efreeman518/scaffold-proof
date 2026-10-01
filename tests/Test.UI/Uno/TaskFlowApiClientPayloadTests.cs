using EF.Testing.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using TaskFlow.Uno.Core.Client;

namespace Test.UI.Uno;

/// <summary>
/// Validates the hand-authored <c>TaskFlowApiClient</c> outgoing request shape: create assigns a
/// client-generated UUIDv7 id when the caller leaves it unset, and every PUT/DELETE sends the
/// required If-Match header (D-021/GR-16).
/// Pure-unit tier: a capturing <see cref="System.Net.Http.HttpMessageHandler"/> records the request
/// without ever opening a socket - payload-shape regression coverage for client serialization rules.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class TaskFlowApiClientPayloadTests
{
    /// <summary>Verifies TaskItems.PostAsync assigns a UUIDv7 id when the caller left it null (idempotent create).</summary>
    [TestMethod]
    public async Task TaskItemsPostAsync_AssignsUuidV7Id_WhenMissing()
    {
        var handler = CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);

        var dto = new TaskItemDto { Title = "New Task", Priority = "Medium" };

        await apiClient.Api.TaskItems.PostAsync(dto, TestContext.CancellationToken);

        Assert.IsNotNull(handler.Requests[^1].Body);
        using var doc = JsonDocument.Parse(handler.Requests[^1].Body!);
        var id = doc.RootElement.GetProperty("item").GetProperty("id").GetString();
        Assert.IsNotNull(id);
        var guid = Guid.Parse(id!);
        Assert.AreEqual(7, (guid.ToByteArray()[7] >> 4) & 0x0F, "The client-generated id must be a UUIDv7 (version nibble 7).");
    }

    /// <summary>Verifies TaskItems[id].PutAsync sends the caller's Version as a quoted If-Match header.</summary>
    [TestMethod]
    public async Task TaskItemsPutAsync_SendsIfMatchHeader()
    {
        var handler = CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);

        var taskId = Guid.NewGuid();
        var dto = new TaskItemDto { Id = taskId, Title = "Existing Task", Priority = "High" };

        await apiClient.Api.TaskItems[taskId].PutAsync(dto, "42", TestContext.CancellationToken);

        Assert.IsNotNull(LastIfMatch(handler));
        Assert.AreEqual("\"42\"", LastIfMatch(handler));
    }

    /// <summary>Verifies TaskItems[id].Comments.DeleteAsync sends the root's Version as a quoted If-Match header.</summary>
    [TestMethod]
    public async Task CommentsDeleteAsync_SendsRootVersionAsIfMatchHeader()
    {
        var handler = CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);

        await apiClient.Api.TaskItems[Guid.NewGuid()].Comments.DeleteAsync(Guid.NewGuid(), "3", TestContext.CancellationToken);

        Assert.IsNotNull(LastIfMatch(handler));
        Assert.AreEqual("\"3\"", LastIfMatch(handler));
    }

    /// <summary>Verifies the "*" trusted-automation wildcard is sent unquoted (D-032).</summary>
    [TestMethod]
    public async Task DeleteAsync_WithWildcard_SendsUnquotedAsterisk()
    {
        var handler = CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);

        await apiClient.Api.TaskItems[Guid.NewGuid()].DeleteAsync("*", TestContext.CancellationToken);

        Assert.AreEqual("*", LastIfMatch(handler));
    }

    /// <summary>Records each request and answers with a fresh task item payload.</summary>
    private static StubHttpMessageHandler CaptureRequestHandler() => new((_, _, _) =>
    {
        var responseJson = "{\"item\":{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"x\",\"priority\":\"Medium\",\"status\":\"Open\"}}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
    });

    private static string? LastIfMatch(StubHttpMessageHandler handler) =>
        handler.Requests[^1].Headers.TryGetValue("If-Match", out var values) ? values[0] : null;

    public TestContext TestContext { get; set; } = null!;
}
