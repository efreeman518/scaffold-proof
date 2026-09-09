using Aspire.Hosting.Testing;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using TaskFlow.Application.Models;

namespace Test.Aspire;

/// <summary>
/// Aspire mesh smoke coverage for app-facing resources that the test graph now includes by default.
/// </summary>
[TestClass]
[TestCategory("Aspire")]
[DoNotParallelize]
public class AppSurfaceAspireTests
{
    /// <summary>Boots the shared Aspire graph before app-surface checks run.</summary>
    [ClassInitialize]
    public static Task ClassInit(TestContext context) => AspireTestHost.EnsureStartedAsync(context);

    /// <summary>Verifies the Gateway project is part of the default Aspire test graph.</summary>
    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_AppHost_When_GatewayRootRequested_Then_GatewayResponds()
    {
        var ct = TestContext.CancellationToken;
        await AspireTestHost.WaitForResourceHealthyAsync("taskflowgateway", ct);

        using var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowgateway", "http");
        using var response = await client.GetAsync("/", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("TaskFlow Gateway", body);
    }

    /// <summary>
    /// Verifies that given a category created and then re-fetched through the gateway's YARP proxy,
    /// when both requests cross the <c>taskflowgateway</c> -> <c>taskflowapi</c> hop, then the API's
    /// <c>ETag</c> response header (aggregate Version, D-0xx) is not stripped or rewritten by YARP.
    /// </summary>
    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_AppHost_When_CategoryRequestedThroughGateway_Then_ETagSurvivesTheYarpHop()
    {
        var ct = TestContext.CancellationToken;
        await AspireTestHost.WaitForResourceHealthyAsync("taskflowgateway", ct);
        await AspireTestHost.WaitForResourceHealthyAsync("taskflowapi", ct);

        using var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowgateway", "http");
        client.Timeout = TimeSpan.FromMinutes(5);

        var createRequest = new DefaultRequest<CategoryDto>
        {
            Item = new CategoryDto
            {
                Name = $"Gateway ETag {Guid.NewGuid():N}",
                Description = "Created through the gateway to verify the ETag survives the YARP hop",
                SortOrder = 1,
                IsActive = true
            }
        };

        using var createResponse = await client.PostAsJsonAsync("/api/v1/categories", createRequest, ct);
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        var createdEtag = createResponse.Headers.ETag;
        Assert.IsNotNull(createdEtag, "POST through the gateway is expected to carry the API's ETag response header.");

        var createdBody = await createResponse.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(cancellationToken: ct);
        var id = createdBody!.Item!.Id;

        using var getResponse = await client.GetAsync($"/api/v1/categories/{id}", ct);
        Assert.AreEqual(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.AreEqual(createdEtag!.Tag, getResponse.Headers.ETag?.Tag,
            "GET through the gateway should return the same ETag the API set on create.");
    }

    /// <summary>Verifies the Blazor project is part of the default Aspire test graph.</summary>
    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_AppHost_When_BlazorRootRequested_Then_BlazorHostResponds()
    {
        var ct = TestContext.CancellationToken;
        await AspireTestHost.WaitForResourceHealthyAsync("taskflowblazor", ct);

        // TaskFlow.Blazor unconditionally calls app.UseHttpsRedirection() (production behavior, not test-only),
        // so a request to its "http" endpoint gets a 307 to the https endpoint. On the ubuntu CI runner that
        // https endpoint carries the untrusted ASP.NET dev certificate, and CreateHttpClient's default handler
        // follows the redirect and fails the TLS handshake. Accept the dev cert on this test-only handler only -
        // production code and every other surface test (Gateway, API, Uno WASM, which do not redirect) are
        // untouched.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = AspireTestHost.AspireApp!.GetEndpoint("taskflowblazor", "http")
        };
        using var response = await client.GetAsync("/", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html", body);
    }

    /// <summary>Verifies the React Vite project is included when node modules are present.</summary>
    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_AppHost_When_ReactIsRunnable_Then_ReactHostResponds()
    {
        if (!AspireTestHost.ReactAvailable)
        {
            if (IsExplicitlyDisabled("TASKFLOW_REACT_TESTS_ENABLED"))
                Assert.Inconclusive("TASKFLOW_REACT_TESTS_ENABLED=false - React full-stack smoke opted out.");

            Assert.Fail("React host prerequisites are missing. Run npm ci in src/UI/TaskFlow.React or set TASKFLOW_REACT_TESTS_ENABLED=false to opt out explicitly.");
        }

        var ct = TestContext.CancellationToken;
        await AspireTestHost.WaitForResourceHealthyAsync("taskflowreact", ct);

        using var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowreact", "http");
        using var response = await client.GetAsync("/", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("root", body);
    }

    /// <summary>Verifies the Uno WASM static host is included when built assets are present.</summary>
    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Given_AppHost_When_UnoWasmIsRunnable_Then_UnoHostResponds()
    {
        if (!AspireTestHost.UnoWasmAvailable)
        {
            if (IsExplicitlyDisabled("TASKFLOW_WASM_TESTS_ENABLED"))
                Assert.Inconclusive("TASKFLOW_WASM_TESTS_ENABLED=false - Uno WASM full-stack smoke opted out.");

            Assert.Fail("Uno WASM assets are missing. Build the browserwasm target or set TASKFLOW_WASM_TESTS_ENABLED=false to opt out explicitly.");
        }

        var ct = TestContext.CancellationToken;
        await AspireTestHost.WaitForResourceHealthyAsync("taskflowuno", ct);

        using var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowuno", "http");
        using var response = await client.GetAsync("/", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html", body);
    }

    /// <summary>Gets MSTest context for cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    private static bool IsExplicitlyDisabled(string variableName) =>
        string.Equals(Environment.GetEnvironmentVariable(variableName), "false", StringComparison.OrdinalIgnoreCase);
}
