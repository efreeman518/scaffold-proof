using Moq;
using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Business.Services;
using TaskFlow.Uno.Core.Client;

namespace Test.UI.Uno;

/// <summary>
/// Validates <c>TagApiService</c> against <c>MockHttpMessageHandler</c>: CRUD methods map Kiota payloads
/// to the Uno <c>TagModel</c>.
/// Pure-unit tier: in-process <c>HttpClient</c> with mock handler - no real server.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class TagApiServiceTests
{
    private MockHttpMessageHandler _handler = null!;
    private HttpClient _httpClient = null!;
    private TaskFlowApiClient _apiClient = null!;
    private TagApiService _service = null!;

    /// <summary>Prepares per-test fixtures so each test starts from a predictable state.</summary>
    [TestInitialize]
    public void Setup()
    {
        _handler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://localhost:7200") };
        _apiClient = new TaskFlowApiClient(_httpClient);
        _service = new TagApiService(_apiClient, Mock.Of<INotificationService>());
    }

    /// <summary>Verifies teardown behavior and protects the expected test contract.</summary>
    [TestCleanup]
    public void Teardown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    /// <summary>Verifies search returns mapped models behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SearchAsync_ReturnsMappedModels()
    {
        var results = await _service.SearchAsync(ct: TestContext.CancellationToken);

        Assert.IsNotEmpty(results);
        // Mock SearchTags orders alphabetically - "backend" sorts before "frontend".
        Assert.AreEqual("backend", results[0].Name);
        Assert.AreEqual("#10B981", results[0].Color);
    }

    /// <summary>Verifies get returns mapped model behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task GetAsync_ReturnsMappedModel()
    {
        var tagId = Guid.Parse("22222222-2222-2222-2222-111111111111");

        var result = await _service.GetAsync(tagId, TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.AreEqual("frontend", result.Name);
        Assert.AreEqual(tagId, result.Id);
    }

    /// <summary>Creates returns mapped model used by the surrounding test cases.</summary>
    [TestMethod]
    public async Task CreateAsync_ReturnsMappedModel()
    {
        var newTag = new TagModel { Name = "urgent", Color = "#EF4444" };

        var result = await _service.CreateAsync(newTag, TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Id);
        Assert.AreEqual("urgent", result.Name);
    }

    /// <summary>Verifies update returns mapped model behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task UpdateAsync_ReturnsMappedModel()
    {
        var tag = new TagModel
        {
            Id = Guid.Parse("22222222-2222-2222-2222-111111111111"),
            Name = "updated-tag",
            Color = "#000000"
        };

        var result = await _service.UpdateAsync(tag, expectedVersion: 1, TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Id);
    }

    /// <summary>Verifies delete does not throw behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task DeleteAsync_DoesNotThrow()
    {
        await _service.DeleteAsync(Guid.NewGuid(), expectedVersion: null, TestContext.CancellationToken);
    }

    public TestContext TestContext { get; set; } = null!;
}
