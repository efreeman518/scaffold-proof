using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Test.UI.WasmHost;

/// <summary>
/// HTTP-level contract of the Uno WASM static host (<c>TaskFlow.Uno.WasmHost</c>), booted in-process against a
/// minimal published-output fixture: precompressed assets are negotiated by Accept-Encoding quality, fingerprinted
/// assets are immutable, the document and client routes revalidate, a missing asset is a 404 rather than the SPA
/// shell, client routes fall back to index.html, and <c>/app-config.json</c> carries the gateway base URL uncached.
/// The asserts are on behavior, not on header spelling, so they hold for any implementation of the host.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class WasmHostHttpContractTests
{
    private const string GatewayBaseUrl = "https://gateway.example.test";
    private const string PackageFolder = "package_abcdef1234567890";
    private const string AppAssemblyRoute = "/_framework/TaskFlow.Uno.abcdefgh.wasm";
    private const string IndexMarker = "taskflow-uno-index";

    private static string _distPath = null!;
    private static WebApplicationFactory<Program> _factory = null!;
    private static HttpClient _client = null!;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _distPath = CreatePublishedOutputFixture();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:BaseUrl", GatewayBaseUrl);
            builder.UseSetting("UnoWasm:DistPath", _distPath);
        });
        _client = _factory.CreateClient();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
        Directory.Delete(_distPath, recursive: true);
    }

    [TestMethod]
    [DataRow("br, gzip", "br")]
    [DataRow("gzip, br", "br")]
    [DataRow("br;q=0.2, gzip;q=0.9", "gzip")]
    [DataRow("gzip", "gzip")]
    public async Task PrecompressedAsset_IsSelectedByAcceptEncoding(string acceptEncoding, string expectedEncoding)
    {
        using var response = await GetAsync(AppAssemblyRoute, acceptEncoding);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { expectedEncoding }, response.Content.Headers.ContentEncoding.ToArray());
        Assert.AreEqual(Marker(expectedEncoding), await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual("application/wasm", response.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(
            response.Headers.Vary.Contains("Accept-Encoding", StringComparer.OrdinalIgnoreCase),
            "a cache must key the negotiated representation on Accept-Encoding");
    }

    [TestMethod]
    public async Task PrecompressedAsset_WithoutAcceptEncoding_IsServedUnencoded()
    {
        using var response = await GetAsync(AppAssemblyRoute, acceptEncoding: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsEmpty(response.Content.Headers.ContentEncoding);
        Assert.AreEqual(Marker("identity"), await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("br, gzip")]
    public async Task FingerprintedAsset_IsImmutable(string? acceptEncoding)
    {
        using var response = await GetAsync(AppAssemblyRoute, acceptEncoding);

        var cacheControl = response.Headers.CacheControl;
        Assert.IsNotNull(cacheControl);
        Assert.AreEqual(TimeSpan.FromDays(365), cacheControl.MaxAge);
        Assert.IsTrue(cacheControl.Extensions.Any(e => e.Name == "immutable"), cacheControl.ToString());
        Assert.IsFalse(cacheControl.NoCache, cacheControl.ToString());
    }

    [TestMethod]
    public async Task UnfingerprintedAsset_Revalidates()
    {
        using var response = await GetAsync($"/{PackageFolder}/require.js", acceptEncoding: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        AssertRevalidates(response);
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/index.html")]
    [DataRow("/tasks/active")]
    [DataRow("/categories/7f7e1c2a/edit")]
    public async Task DocumentAndClientRoutes_ServeIndexAndRevalidate(string path)
    {
        using var response = await GetAsync(path, acceptEncoding: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(TestContext.CancellationToken), IndexMarker);
        AssertRevalidates(response);
    }

    [TestMethod]
    [DataRow("/_framework/missing.wasm")]
    [DataRow("/missing.js")]
    [DataRow("/_framework/TaskFlow.Uno.zzzzzzzz.wasm")]
    public async Task MissingAsset_IsNotFound_NotTheSpaShell(string path)
    {
        using var response = await GetAsync(path, acceptEncoding: "br, gzip");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task AppConfig_ReturnsTheGatewayBaseUrlUncached()
    {
        using var response = await GetAsync("/app-config.json", acceptEncoding: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore, response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(GatewayBaseUrl, body.RootElement.GetProperty("gatewayBaseUrl").GetString());
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await _client.SendAsync(request, TestContext.CancellationToken);
    }

    private static void AssertRevalidates(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        Assert.IsNotNull(cacheControl);
        Assert.IsTrue(cacheControl.NoCache, cacheControl.ToString());
        Assert.IsFalse(cacheControl.Extensions.Any(e => e.Name == "immutable"), cacheControl.ToString());
    }

    private static string Marker(string encoding) => $"app-assembly-{encoding}";

    /// <summary>
    /// The smallest output <c>PublishedAssetContract.Validate</c> accepts: index.html referencing the package's
    /// require.js and uno-bootstrap.css, and exactly one fingerprinted application assembly, here with brotli and
    /// gzip siblings. Each representation has distinct content so the test can tell which one was served.
    /// </summary>
    private static string CreatePublishedOutputFixture()
    {
        var dist = Path.Combine(Path.GetTempPath(), $"taskflow-wasmhost-{Guid.NewGuid():N}");
        var wwwroot = Path.Combine(dist, "wwwroot");
        var package = Path.Combine(wwwroot, PackageFolder);
        var framework = Path.Combine(wwwroot, "_framework");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(framework);

        File.WriteAllText(Path.Combine(wwwroot, "index.html"), $"""
            <!doctype html>
            <html><head>
            <link href="/{PackageFolder}/uno-bootstrap.css" rel="stylesheet">
            <script src="/{PackageFolder}/require.js"></script>
            </head><body>{IndexMarker}</body></html>
            """);
        File.WriteAllText(Path.Combine(package, "require.js"), "// require");
        File.WriteAllText(Path.Combine(package, "uno-bootstrap.css"), "/* bootstrap */");

        var appAssembly = Path.Combine(framework, Path.GetFileName(AppAssemblyRoute));
        File.WriteAllText(appAssembly, Marker("identity"));
        File.WriteAllText(appAssembly + ".br", Marker("br"));
        File.WriteAllText(appAssembly + ".gz", Marker("gzip"));
        return dist;
    }
}
