using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Test.Endpoints;

/// <summary>Covers open API endpoint behavior with focused assertions that document expected behavior and regression intent.</summary>
[TestClass]
public sealed class OpenApiEndpointTests
{
    private static CustomApiFactory _factory = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _factory = new CustomApiFactory();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _factory?.Dispose();

    /// <summary>Verifies that given open API enabled, when get v 1 document, then contains versioned domain routes.</summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_OpenApiEnabled_When_GetV1Document_Then_ContainsVersionedDomainRoutes()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual("v1", document.RootElement.GetProperty("info").GetProperty("version").GetString());

        var paths = document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .ToArray();

        Assert.Contains(
            path => path.StartsWith("/api/v1/task-items", StringComparison.Ordinal), paths,
            "OpenAPI v1 document must include versioned domain API routes.");

        Assert.DoesNotContain(
            path => path.StartsWith("/health", StringComparison.Ordinal)
                || path.StartsWith("/alive", StringComparison.Ordinal)
                || path.StartsWith("/api/flowengine", StringComparison.Ordinal),
            paths,
            "OpenAPI v1 document should not include unversioned operational/admin routes.");
    }

    /// <summary>
    /// Verifies the document publishes the concurrency contract: without it, generated clients would
    /// omit If-Match and every write they make would come back 428.
    /// </summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_MutatingRoute_When_GetV1Document_Then_DeclaresIfMatchAnd412And428()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        var put = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/categories/{id}")
            .GetProperty("put");

        Assert.Contains(
            p => p.GetProperty("name").GetString() == "If-Match" && p.GetProperty("in").GetString() == "header",
            put.GetProperty("parameters").EnumerateArray(),
            "A route that requires If-Match must declare the header.");

        var responses = put.GetProperty("responses");
        Assert.IsTrue(responses.TryGetProperty("412", out _), "The document must declare 412 for a stale write.");
        Assert.IsTrue(responses.TryGetProperty("428", out _), "The document must declare 428 for a missing precondition.");
        Assert.IsTrue(
            responses.GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _),
            "A success response must declare the ETag header clients need for the next write.");
    }

    /// <summary>
    /// Drift guard for client codegen (Refitter, openapi-typescript): both read the committed
    /// src/Host/TaskFlow.Api/openapi-doc/TaskFlow.Api.json build artifact offline, so a hand-edited
    /// endpoint that forgot to rebuild would silently ship stale generated clients without this.
    /// </summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_CommittedOpenApiDocument_When_ComparedToRuntimeDocument_Then_Matches()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var runtimeJson = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        var runtimeDocument = JsonNode.Parse(runtimeJson);
        // The "servers" entry is synthesized per-request from the live host address (TestServer reports
        // "http://localhost/"); build-time generation has no request context to derive it from, so it is
        // absent there. Not part of the contract shape client codegen cares about - drop it on both sides.
        runtimeDocument?.AsObject().Remove("servers");

        var committedPath = Path.Combine(AppContext.BaseDirectory, "openapi-doc", "TaskFlow.Api.json");
        Assert.IsTrue(File.Exists(committedPath), $"Committed OpenAPI document missing at {committedPath}");
        var committedDocument = JsonNode.Parse(await File.ReadAllTextAsync(committedPath, TestContext.CancellationToken));
        committedDocument?.AsObject().Remove("servers");

        Assert.IsTrue(
            JsonNode.DeepEquals(committedDocument, runtimeDocument),
            "src/Host/TaskFlow.Api/openapi-doc/TaskFlow.Api.json is stale. Rebuild TaskFlow.Api " +
            "(ASPNETCORE_ENVIRONMENT=Development, see docs/plans/client-generation.md) and commit the refreshed document.");
    }

    public TestContext TestContext { get; set; } = null!;
}
