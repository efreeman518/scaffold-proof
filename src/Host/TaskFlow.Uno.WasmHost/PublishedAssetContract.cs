using System.Text.RegularExpressions;

namespace TaskFlow.Uno.WasmHost;

internal static partial class PublishedAssetContract
{
    /// <summary>The SDK's static web assets endpoint manifest the host maps with <c>MapStaticAssets</c>.</summary>
    internal const string EndpointsManifestFileName = "TaskFlow.Uno.staticwebassets.endpoints.json";

    internal static string Validate(string distPath)
    {
        var webRootPath = Path.Combine(Path.GetFullPath(distPath), "wwwroot");
        var indexPath = Path.Combine(webRootPath, "index.html");
        if (!File.Exists(indexPath))
        {
            throw new InvalidOperationException(
                $"Uno WASM publish output is missing '{indexPath}'. Publish Release output before starting the host.");
        }

        var indexHtml = File.ReadAllText(indexPath);
        foreach (var requiredAsset in new[] { "require.js", "uno-bootstrap.css" })
        {
            var reference = PackageAssetReference().Matches(indexHtml)
                .Select(match => match.Groups["path"].Value)
                .FirstOrDefault(path => path.EndsWith('/' + requiredAsset, StringComparison.OrdinalIgnoreCase));
            if (reference is null)
            {
                throw new InvalidOperationException(
                    $"Uno WASM publish index must reference a package '{requiredAsset}' asset.");
            }

            var assetPath = Path.Combine(
                webRootPath,
                reference.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(assetPath))
            {
                throw new InvalidOperationException(
                    $"Uno WASM publish output is missing the package asset referenced by index.html: '{assetPath}'.");
            }
        }

        var frameworkPath = Path.Combine(webRootPath, "_framework");
        var appAssemblies = Directory.Exists(frameworkPath)
            ? Directory.EnumerateFiles(frameworkPath, "TaskFlow.Uno.*.wasm", SearchOption.TopDirectoryOnly)
                .Where(path => AppAssemblyFileName().IsMatch(Path.GetFileName(path)))
                .ToArray()
            : [];

        if (appAssemblies.Length != 1)
        {
            throw new InvalidOperationException(
                $"Uno WASM publish output must contain exactly one current TaskFlow.Uno application assembly in '{frameworkPath}', but found {appAssemblies.Length}.");
        }

        var endpointsManifestPath = Path.Combine(Path.GetFullPath(distPath), EndpointsManifestFileName);
        if (!File.Exists(endpointsManifestPath))
        {
            throw new InvalidOperationException(
                $"Uno WASM publish output is missing its static web assets endpoint manifest '{endpointsManifestPath}'.");
        }

        return webRootPath;
    }

    [GeneratedRegex(@"^TaskFlow\.Uno\.[A-Za-z0-9]+\.wasm$", RegexOptions.CultureInvariant)]
    private static partial Regex AppAssemblyFileName();

    [GeneratedRegex("""(?:src|href)\s*=\s*["'](?<path>/package_[A-Fa-f0-9]+/(?:require\.js|uno-bootstrap\.css))["']""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PackageAssetReference();
}
