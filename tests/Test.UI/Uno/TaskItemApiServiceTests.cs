using Moq;
using System.Net;
using System.Text;
using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Business.Services;
using TaskFlow.Uno.Core.Client;

namespace Test.UI.Uno;

/// <summary>
/// Validates <c>TaskItemApiService</c> against <c>MockHttpMessageHandler</c> plus a capturing handler
/// that asserts the client sends the required If-Match header on update/delete.
/// Pure-unit tier: in-process <c>HttpClient</c> with mock/capture handlers - no real server.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class TaskItemApiServiceTests
{
    private MockHttpMessageHandler _handler = null!;
    private HttpClient _httpClient = null!;
    private TaskFlowApiClient _apiClient = null!;
    private TaskItemApiService _service = null!;

    /// <summary>Prepares per-test fixtures so each test starts from a predictable state.</summary>
    [TestInitialize]
    public void Setup()
    {
        _handler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://localhost:7200") };
        _apiClient = new TaskFlowApiClient(_httpClient);
        _service = new TaskItemApiService(_apiClient, Mock.Of<INotificationService>());
    }

    /// <summary>Verifies teardown behavior and protects the expected test contract.</summary>
    [TestCleanup]
    public void Teardown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    /// <summary>Verifies the cursor page returns mapped models and reports whether more pages remain.</summary>
    [TestMethod]
    public async Task SearchCursorAsync_ReturnsMappedModelsAndCursor()
    {
        var page = await _service.SearchCursorAsync(pageSize: 5, ct: TestContext.CancellationToken);

        Assert.IsTrue(page.Items.Count > 0);
        Assert.AreEqual("Build dashboard UI", page.Items[0].Title);
        Assert.AreEqual("InProgress", page.Items[0].Status);
        Assert.AreEqual("High", page.Items[0].Priority);
        Assert.IsTrue(page.HasMore, "The mock seeds more than pageSize tasks, so the first page must report HasMore.");
        Assert.IsNotNull(page.NextCursor);
    }

    /// <summary>Verifies walking the cursor forward with the previous page's NextCursor advances the window.</summary>
    [TestMethod]
    public async Task SearchCursorAsync_WalksCursorForward()
    {
        var first = await _service.SearchCursorAsync(pageSize: 5, ct: TestContext.CancellationToken);
        var second = await _service.SearchCursorAsync(pageSize: 5, cursor: first.NextCursor, ct: TestContext.CancellationToken);

        CollectionAssert.AreNotEqual(
            first.Items.Select(i => i.Id).ToList(),
            second.Items.Select(i => i.Id).ToList(),
            "The second cursor page must not repeat the first page's rows.");
    }

    /// <summary>Verifies search includes overdue task behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SearchCursorAsync_IncludesOverdueTask()
    {
        var page = await _service.SearchCursorAsync(pageSize: 50, ct: TestContext.CancellationToken);

        var overdueTask = page.Items.FirstOrDefault(t => t.Title == "Fix login validation");
        Assert.IsNotNull(overdueTask);
        Assert.IsTrue(overdueTask.IsOverdue);
    }

    /// <summary>Creates returns mapped model used by the surrounding test cases.</summary>
    [TestMethod]
    public async Task CreateAsync_ReturnsMappedModel()
    {
        var newTask = new TaskItemModel { Title = "Test task", Priority = "Medium" };

        var result = await _service.CreateAsync(newTask, TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Id);
    }

    /// <summary>Verifies UpdateAsync sends the entity's Version as the If-Match header (D-021/GR-16).</summary>
    [TestMethod]
    public async Task UpdateAsync_SendsVersionAsIfMatchHeader()
    {
        var captureHandler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(captureHandler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);
        var service = new TaskItemApiService(apiClient, Mock.Of<INotificationService>());

        var model = new TaskItemModel { Id = Guid.NewGuid(), Title = "Existing task", Priority = "Medium" };

        await service.UpdateAsync(model, expectedVersion: 7, TestContext.CancellationToken);

        Assert.IsNotNull(captureHandler.LastIfMatch);
        Assert.AreEqual("\"7\"", captureHandler.LastIfMatch);
    }

    /// <summary>Verifies a null expectedVersion sends the wildcard If-Match ("*"), the trusted-automation override.</summary>
    [TestMethod]
    public async Task DeleteAsync_WithNullExpectedVersion_SendsWildcardIfMatch()
    {
        var captureHandler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(captureHandler) { BaseAddress = new Uri("https://localhost:7200") };
        var apiClient = new TaskFlowApiClient(httpClient);
        var service = new TaskItemApiService(apiClient, Mock.Of<INotificationService>());

        await service.DeleteAsync(Guid.NewGuid(), expectedVersion: null, TestContext.CancellationToken);

        Assert.AreEqual("*", captureHandler.LastIfMatch);
    }

    /// <summary>Supports test execution for Test.unit Uno scenarios.</summary>
    private sealed class CaptureRequestHandler : HttpMessageHandler
    {
        public string? LastIfMatch { get; private set; }

        /// <summary>Verifies send behavior and protects the expected test contract.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastIfMatch = request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.First().Tag : null;

            var responseJson = "{\"item\":{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"Updated\",\"priority\":\"Medium\",\"status\":\"Open\",\"version\":8}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
