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
        var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);

        var dto = new TaskItemDto { Title = "New Task", Priority = "Medium" };

        await apiClient.Api.TaskItems.PostAsync(dto, TestContext.CancellationToken);

        Assert.IsNotNull(handler.LastRequestBody);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var id = doc.RootElement.GetProperty("item").GetProperty("id").GetString();
        Assert.IsNotNull(id);
        var guid = Guid.Parse(id!);
        Assert.AreEqual(7, (guid.ToByteArray()[7] >> 4) & 0x0F, "The client-generated id must be a UUIDv7 (version nibble 7).");
    }

    /// <summary>Verifies TaskItems[id].PutAsync sends the caller's Version as a quoted If-Match header.</summary>
    [TestMethod]
    public async Task TaskItemsPutAsync_SendsIfMatchHeader()
    {
        var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);

        var taskId = Guid.NewGuid();
        var dto = new TaskItemDto { Id = taskId, Title = "Existing Task", Priority = "High" };

        await apiClient.Api.TaskItems[taskId].PutAsync(dto, "42", TestContext.CancellationToken);

        Assert.IsNotNull(handler.LastIfMatch);
        Assert.AreEqual("\"42\"", handler.LastIfMatch);
    }

    /// <summary>Verifies TaskItems[id].Comments.DeleteAsync sends the root's Version as a quoted If-Match header.</summary>
    [TestMethod]
    public async Task CommentsDeleteAsync_SendsRootVersionAsIfMatchHeader()
    {
        var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);

        await apiClient.Api.TaskItems[Guid.NewGuid()].Comments.DeleteAsync(Guid.NewGuid(), "3", TestContext.CancellationToken);

        Assert.IsNotNull(handler.LastIfMatch);
        Assert.AreEqual("\"3\"", handler.LastIfMatch);
    }

    /// <summary>Verifies the "*" trusted-automation wildcard is sent unquoted (D-032).</summary>
    [TestMethod]
    public async Task DeleteAsync_WithWildcard_SendsUnquotedAsterisk()
    {
        var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);

        await apiClient.Api.TaskItems[Guid.NewGuid()].DeleteAsync("*", TestContext.CancellationToken);

        Assert.AreEqual("*", handler.LastIfMatch);
    }

    /// <summary>Supports test execution for Test.unit Uno scenarios.</summary>
    private sealed class CaptureRequestHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public string? LastIfMatch { get; private set; }

        /// <summary>Verifies send behavior and protects the expected test contract.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastIfMatch = request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.First().Tag : null;

            var responseJson = "{\"item\":{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"x\",\"priority\":\"Medium\",\"status\":\"Open\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
