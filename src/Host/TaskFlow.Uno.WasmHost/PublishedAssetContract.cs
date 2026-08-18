using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

namespace TaskFlow.Uno.WasmHost;

internal static partial class PublishedAssetContract
{
    internal const string ImmutableCacheControl = "public,max-age=31536000,immutable";
    internal const string RevalidateCacheControl = "no-cache,must-revalidate";

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

        return webRootPath;
    }

    internal static void AddPrecompressedContentTypes(FileExtensionContentTypeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.Mappings[".br"] = "application/octet-stream";
        provider.Mappings[".gz"] = "application/octet-stream";
    }

    internal static string CacheControlFor(string requestPath) =>
        FingerprintedFileName().IsMatch(Path.GetFileName(OriginalPath(requestPath)))
            ? ImmutableCacheControl
            : RevalidateCacheControl;

    internal static bool LooksLikeAssetRequest(string requestPath) =>
        Path.HasExtension(requestPath.TrimEnd('/'));

    internal static string OriginalPath(string requestPath) =>
        requestPath.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
            ? requestPath[..^3]
            : requestPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? requestPath[..^3]
                : requestPath;

    internal static string? SelectEncoding(string? acceptEncoding, bool brotliExists, bool gzipExists)
    {
        if (string.IsNullOrWhiteSpace(acceptEncoding))
        {
            return null;
        }

        var qualities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double? wildcardQuality = null;
        foreach (var rawEntry in acceptEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segments = rawEntry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var quality = 1d;
            foreach (var parameter in segments.Skip(1))
            {
                var pair = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length == 2 && string.Equals(pair[0], "q", StringComparison.OrdinalIgnoreCase))
                {
                    if (!double.TryParse(pair[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality)
                        || quality is < 0 or > 1)
                    {
                        quality = 0;
                    }
                }
            }

            if (segments[0] == "*")
            {
                wildcardQuality = quality;
            }
            else
            {
                qualities[segments[0]] = quality;
            }
        }

        var brotliQuality = brotliExists ? QualityFor("br") : 0;
        var gzipQuality = gzipExists ? QualityFor("gzip") : 0;
        if (brotliQuality <= 0 && gzipQuality <= 0)
        {
            return null;
        }

        return brotliQuality >= gzipQuality ? "br" : "gzip";

        double QualityFor(string encoding) =>
            qualities.TryGetValue(encoding, out var explicitQuality)
                ? explicitQuality
                : wildcardQuality ?? 0;
    }

    [GeneratedRegex(@"^TaskFlow\.Uno\.[A-Za-z0-9]+\.wasm$", RegexOptions.CultureInvariant)]
    private static partial Regex AppAssemblyFileName();

    [GeneratedRegex(@"\.[A-Za-z0-9]{8,}\.[^.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintedFileName();

    [GeneratedRegex("""(?:src|href)\s*=\s*["'](?<path>/package_[A-Fa-f0-9]+/(?:require\.js|uno-bootstrap\.css))["']""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PackageAssetReference();
}
