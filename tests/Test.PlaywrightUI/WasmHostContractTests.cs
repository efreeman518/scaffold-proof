using TaskFlow.Uno.WasmHost;

namespace Test.PlaywrightUI;

[TestClass]
[TestCategory("Unit")]
public sealed class WasmHostContractTests
{
    [TestMethod]
    public void Validate_RequiresIndexPackageAssetsOneApplicationAssemblyAndTheEndpointManifest()
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
            Assert.Throws<InvalidOperationException>(() => PublishedAssetContract.Validate(root));

            File.WriteAllText(Path.Combine(root, PublishedAssetContract.EndpointsManifestFileName), "{}");
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
