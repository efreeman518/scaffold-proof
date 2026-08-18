using TaskFlow.Uno.WasmHost;
using Microsoft.AspNetCore.StaticFiles;

namespace Test.PlaywrightUI;

[TestClass]
[TestCategory("Unit")]
public sealed class WasmHostContractTests
{
    [TestMethod]
    public void SelectEncoding_RespectsQualityValuesAndServerPreference()
    {
        Assert.AreEqual("gzip", PublishedAssetContract.SelectEncoding("br;q=0.2, gzip;q=0.9", true, true));
        Assert.AreEqual("br", PublishedAssetContract.SelectEncoding("gzip, br", true, true));
        Assert.IsNull(PublishedAssetContract.SelectEncoding("br;q=0, gzip;q=0", true, true));
        Assert.AreEqual("gzip", PublishedAssetContract.SelectEncoding("*;q=0.5, br;q=0", true, true));
    }

    [TestMethod]
    public void CacheControl_OnlyTreatsFingerprintedFilesAsImmutable()
    {
        Assert.AreEqual(
            PublishedAssetContract.ImmutableCacheControl,
            PublishedAssetContract.CacheControlFor("/_framework/TaskFlow.Uno.3ugbcrq8gq.wasm.br"));
        Assert.AreEqual(
            PublishedAssetContract.RevalidateCacheControl,
            PublishedAssetContract.CacheControlFor("/index.html"));
        Assert.AreEqual(
            PublishedAssetContract.RevalidateCacheControl,
            PublishedAssetContract.CacheControlFor("/service-worker.js.gz"));
    }

    [TestMethod]
    public void AssetRouting_DistinguishesMissingAssetsFromClientRoutes()
    {
        Assert.IsTrue(PublishedAssetContract.LooksLikeAssetRequest("/_framework/missing.wasm"));
        Assert.IsFalse(PublishedAssetContract.LooksLikeAssetRequest("/tasks/active"));
    }

    [TestMethod]
    public void PrecompressedContentTypes_AllowStaticFileMiddlewareToServeTransportFiles()
    {
        var provider = new FileExtensionContentTypeProvider();

        PublishedAssetContract.AddPrecompressedContentTypes(provider);

        Assert.IsTrue(provider.TryGetContentType("require.js.br", out var brotliContentType));
        Assert.AreEqual("application/octet-stream", brotliContentType);
        Assert.IsTrue(provider.TryGetContentType("uno-bootstrap.css.gz", out var gzipContentType));
        Assert.AreEqual("application/octet-stream", gzipContentType);
    }

    [TestMethod]
    public void Validate_RequiresIndexAndExactlyOneCurrentApplicationAssembly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"taskflow-wasm-contract-{Guid.NewGuid():N}");
        try
        {
            var framework = Path.Combine(root, "wwwroot", "_framework");
            var package = Path.Combine(root, "wwwroot", "package_abcdef1234567890");
            Directory.CreateDirectory(framework);
            Directory.CreateDirectory(package);
            File.WriteAllText(
                Path.Combine(root, "wwwroot", "index.html"),
                """
                <!doctype html>
                <script src="/package_abcdef1234567890/require.js"></script>
                <link href="/package_abcdef1234567890/uno-bootstrap.css" rel="stylesheet">
                """);

            Assert.Throws<InvalidOperationException>(() => PublishedAssetContract.Validate(root));

            File.WriteAllBytes(Path.Combine(framework, "TaskFlow.Uno.abcdefgh.wasm"), [0]);
            Assert.Throws<InvalidOperationException>(() => PublishedAssetContract.Validate(root));

            File.WriteAllText(Path.Combine(package, "require.js"), string.Empty);
            File.WriteAllText(Path.Combine(package, "uno-bootstrap.css"), string.Empty);
            Assert.AreEqual(Path.Combine(root, "wwwroot"), PublishedAssetContract.Validate(root));

            File.WriteAllBytes(Path.Combine(framework, "TaskFlow.Uno.ijklmnop.wasm"), [0]);
            Assert.Throws<InvalidOperationException>(() => PublishedAssetContract.Validate(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
