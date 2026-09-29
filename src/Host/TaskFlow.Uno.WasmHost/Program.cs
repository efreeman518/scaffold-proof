using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using TaskFlow.Uno.WasmHost;

var builder = WebApplication.CreateBuilder(args);

// Shared Aspire service defaults so this static-asset host reports telemetry (incl. Azure Monitor
// when configured) and health alongside the other .NET hosts. No-ops cleanly without Azure config.
builder.AddServiceDefaults();

var gatewayBaseUrl = builder.Configuration["Gateway:BaseUrl"]
    ?? throw new InvalidOperationException("Gateway:BaseUrl not configured.");
if (!Uri.TryCreate(gatewayBaseUrl, UriKind.Absolute, out var gatewayUri)
    || (gatewayUri.Scheme != Uri.UriSchemeHttp && gatewayUri.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("Gateway:BaseUrl must be an absolute HTTP(S) URL.");
}
gatewayBaseUrl = gatewayUri.ToString().TrimEnd('/');

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

var requirePublishedAssets = (builder.Configuration.GetValue<bool?>("UnoWasm:RequirePublishedAssets")
    ?? builder.Environment.IsProduction())
    || !string.IsNullOrWhiteSpace(configuredDistPath);
string webRootPath;
if (requirePublishedAssets)
{
    webRootPath = PublishedAssetContract.Validate(distPath);
}
else
{
    webRootPath = Path.Combine(distPath, "wwwroot");
    Directory.CreateDirectory(webRootPath);
}

// The Uno output is the web root: MapStaticAssets and MapFallbackToFile both serve from it.
builder.Environment.WebRootPath = webRootPath;
var webRootFileProvider = new PhysicalFileProvider(webRootPath);
builder.Environment.WebRootFileProvider = webRootFileProvider;

var staticWebAssetsManifestPath = Path.Combine(distPath, "TaskFlow.Uno.staticwebassets.runtime.json");
if (File.Exists(staticWebAssetsManifestPath))
{
    builder.Configuration[WebHostDefaults.StaticWebAssetsKey] = staticWebAssetsManifestPath;
    StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
}

// In Development, MapStaticAssets serves a Build-type manifest (the non-published Uno output Aspire runs) with
// no-cache on every route unless this is set. The manifest's own answer (immutable for fingerprinted assets,
// no-cache for the rest) is right here in every environment: a rebuilt asset gets a new fingerprint.
builder.Configuration["EnableStaticAssetsDevelopmentCaching"] = "true";

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/app-config.json", (HttpContext context) =>
{
    context.Response.Headers[HeaderNames.CacheControl] = "no-store";
    return Results.Json(new { gatewayBaseUrl });
});

// The SDK's endpoint manifest for the Uno output: one endpoint per route, with Content-Encoding selectors for
// the .br/.gz siblings (chosen by Accept-Encoding quality), Vary, strong ETags, and max-age=31536000,immutable on
// fingerprinted routes and no-cache on the rest.
var endpointsManifestPath = Path.Combine(distPath, PublishedAssetContract.EndpointsManifestFileName);
if (File.Exists(endpointsManifestPath))
{
    app.MapStaticAssets(endpointsManifestPath);

    // Uno.Wasm.Bootstrap rewrites uno-config.js in the publish output after the SDK wrote this manifest (it patches
    // in the published dotnet.js fingerprint) and deletes the file's .br/.gz siblings, so the manifest entry for it
    // names missing files and a stale length, and a browser asking for br got a failed response. Serve that one file
    // from disk before the manifest endpoint runs, as a plain file server (the nginx image) does. Middleware, not a
    // competing endpoint: route selection prefers the manifest's literal route over any parameterized one.
    app.Use(async (context, next) =>
    {
        var segments = context.Request.Path.Value?.Split('/');
        if (segments is not ["", var package, "uno-config.js"]
            || !package.StartsWith("package_", StringComparison.Ordinal)
            || !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        var file = webRootFileProvider.GetFileInfo($"{package}/uno-config.js");
        if (!file.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        await Results.File(file.PhysicalPath!, "text/javascript").ExecuteAsync(context);
    });
}
else
{
    app.Logger.UnoWasmAssetsNotFound(distPath);
}

// Client routes only: the fallback pattern is {*path:nonfile}, so a request for a missing .wasm or .js is a 404,
// never the SPA shell. The document revalidates so a new deployment is picked up on the next navigation.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache"
});

app.Run();
