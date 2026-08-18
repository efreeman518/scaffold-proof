using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using TaskFlow.Uno.WasmHost;

var builder = WebApplication.CreateBuilder(args);

// Shared Aspire service defaults so this static-asset host reports telemetry (incl. Azure Monitor
// when configured) and health alongside the other .NET hosts. No-ops cleanly without Azure config.
builder.AddServiceDefaults();

var configuredDistPath = builder.Configuration["UnoWasm:DistPath"];
var distPath = configuredDistPath;

if (string.IsNullOrWhiteSpace(distPath))
{
#if DEBUG
    const string configuration = "Debug";
#else
    const string configuration = "Release";
#endif
    distPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..",
        "..",
        "UI",
        "TaskFlow.Uno",
        "bin",
        configuration,
        "net10.0-browserwasm",
        configuration == "Release" ? "publish" : string.Empty));
}
else
{
    distPath = Path.GetFullPath(distPath);
}

var staticWebAssetsManifestPath = Path.Combine(distPath, "TaskFlow.Uno.staticwebassets.runtime.json");
if (File.Exists(staticWebAssetsManifestPath))
{
    builder.Configuration[WebHostDefaults.StaticWebAssetsKey] = staticWebAssetsManifestPath;
    StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
}

var app = builder.Build();

app.MapDefaultEndpoints();

var indexPath = Path.Combine(distPath, "wwwroot", "index.html");
var requirePublishedAssets = (builder.Configuration.GetValue<bool?>("UnoWasm:RequirePublishedAssets")
    ?? app.Environment.IsProduction())
    || !string.IsNullOrWhiteSpace(configuredDistPath);
string webRootPath;
if (requirePublishedAssets)
{
    webRootPath = PublishedAssetContract.Validate(distPath);
}
else
{
    if (!File.Exists(indexPath))
    {
        app.Logger.LogWarning("Uno WASM assets were not found at {DistPath}. Build TaskFlow.Uno for net10.0-browserwasm first.", distPath);
    }

    Directory.CreateDirectory(distPath);
    webRootPath = Path.Combine(distPath, "wwwroot");
    Directory.CreateDirectory(webRootPath);
}

var fileProvider = new PhysicalFileProvider(webRootPath);

var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".dat"] = "application/octet-stream";
contentTypeProvider.Mappings[".pdb"] = "application/octet-stream";
PublishedAssetContract.AddPrecompressedContentTypes(contentTypeProvider);

const string originalAssetPathKey = "TaskFlow.Uno.OriginalAssetPath";
var responseHeaders = new Action<StaticFileResponseContext>(context =>
{
    var requestPath = context.Context.Items.TryGetValue(originalAssetPathKey, out var originalPath)
        ? (string)originalPath!
        : context.Context.Request.Path.Value ?? string.Empty;
    context.Context.Response.Headers[HeaderNames.CacheControl] = PublishedAssetContract.CacheControlFor(requestPath);

    if (context.Context.Items.TryGetValue(originalAssetPathKey, out _)
        && contentTypeProvider.TryGetContentType(requestPath, out var contentType))
    {
        context.Context.Response.ContentType = contentType;
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", distPath }));
app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        await next();
        return;
    }

    var originalPath = context.Request.Path.Value ?? string.Empty;
    var relativePath = originalPath.TrimStart('/');
    var brotliExists = fileProvider.GetFileInfo(relativePath + ".br").Exists;
    var gzipExists = fileProvider.GetFileInfo(relativePath + ".gz").Exists;
    if (brotliExists || gzipExists)
    {
        context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
    }

    var encoding = PublishedAssetContract.SelectEncoding(
        context.Request.Headers.AcceptEncoding.ToString(),
        brotliExists,
        gzipExists);
    if (encoding is not null)
    {
        context.Items[originalAssetPathKey] = originalPath;
        context.Request.Path = originalPath + (encoding == "br" ? ".br" : ".gz");
        context.Response.Headers.ContentEncoding = encoding;
    }

    await next();
});
app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = fileProvider
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = fileProvider,
    ContentTypeProvider = contentTypeProvider,
    OnPrepareResponse = responseHeaders
});
app.Use(async (context, next) =>
{
    if (PublishedAssetContract.LooksLikeAssetRequest(context.Request.Path.Value ?? string.Empty))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    FileProvider = fileProvider,
    ContentTypeProvider = contentTypeProvider,
    OnPrepareResponse = responseHeaders
});

app.Run();
