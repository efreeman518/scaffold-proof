using Moq;
using TaskFlow.Uno.Core.Business.Models;
using EF.UI.Client.Notifications;
using TaskFlow.Uno.Core.Business.Services;
using TaskFlow.Uno.Core.Client;

namespace Test.UI.Uno;

/// <summary>
/// Validates <c>CategoryApiService</c> against <c>MockHttpMessageHandler</c>: search and create map
/// the mock payload to the Uno <c>CategoryModel</c>.
/// Pure-unit tier: in-process <c>HttpClient</c> with mock handler - no real server.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class CategoryApiServiceTests
{
    private MockHttpMessageHandler _handler = null!;
    private HttpClient _httpClient = null!;
    private TaskFlowApiClient _apiClient = null!;
    private CategoryApiService _service = null!;

    /// <summary>Prepares per-test fixtures so each test starts from a predictable state.</summary>
    [TestInitialize]
    public void Setup()
    {
        _handler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://localhost:7200") };
        _apiClient = new TaskFlowApiClient(_httpClient);
        _service = new CategoryApiService(_apiClient, Mock.Of<INotificationService>());
    }

    /// <summary>Verifies teardown behavior and protects the expected test contract.</summary>
    [TestCleanup]
    public void Teardown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    /// <summary>Verifies search returns mapped categories behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SearchAsync_ReturnsMappedCategories()
    {
        var results = await _service.SearchAsync(ct: TestContext.CancellationToken);

        Assert.IsNotEmpty(results);
        Assert.AreEqual("Development", results[0].Name);
        Assert.IsTrue(results[0].IsActive);
    }

    /// <summary>Creates returns mapped model used by the surrounding test cases.</summary>
    [TestMethod]
    public async Task CreateAsync_ReturnsMappedModel()
    {
        var newCategory = new CategoryModel { Name = "Test category", IsActive = true };

        var result = await _service.CreateAsync(newCategory, TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Id);
    }

    public TestContext TestContext { get; set; } = null!;
}
