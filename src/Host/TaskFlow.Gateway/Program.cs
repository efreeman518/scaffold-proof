using EF.AspNetCore.Correlation;
using EF.AspNetCore.Proxy;
using EF.Host;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using TaskFlow.Application.Contracts;
using TaskFlow.Gateway;
using TaskFlow.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Azure App Configuration (D-042): dynamic config, no-op unless AppConfig:Endpoint (or
// ConnectionStrings:AppConfig) is set; EF.Host refreshes it in the background. Runs first so later
// configuration reads see values it overrides. No feature flags: the Gateway evaluates none of the D-042
// flags, so it stays out of the "who may use Microsoft.FeatureManagement" architecture rule.
_ = HostingLaneResolver.Resolve(builder.Configuration);
builder.AddEfAzureAppConfiguration(configure: o =>
{
    o.SentinelKey = "TaskFlow:Sentinel";
    o.UseFeatureFlags = false;
});

builder.AddServiceDefaults();
builder.AddProxyForwarding();
builder.Services.AddGatewayServices(builder.Configuration);
var authMode = AuthModeResolver.Resolve(builder.Configuration[AuthModeResolver.ConfigKey]);

// Authorization (auth registered in AddGatewayServices)
builder.Services.AddAuthorization();

var app = builder.Build();

// Pipeline order: security -> CORS -> middleware -> request timeouts -> endpoints -> reverse proxy
// Adopt the edge proxy's public scheme/host before auth and before YARP re-stamps
// X-Forwarded-* for the downstream app.
app.UseProxyForwarding();
// EF.AspNetCore problem details (AddEfProblemDetails in AddGatewayServices): no exception text on a 5xx outside
// Development, requestId/traceId on every problem.
app.UseExceptionHandler();

app.UseCors("UnoUI");
app.UseCorrelationId();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// D-064: must run before MapReverseProxy so a route's Timeout/TimeoutPolicy metadata applies - YARP reads
// the same ASP.NET Core request-timeout feature MapReverseProxy's routes are checked against.
app.UseRequestTimeouts();

app.MapDefaultEndpoints();
app.MapHealthChecks("/health/full", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("full")
})
.AllowAnonymous()
.RequireRateLimiting("HealthFull");

app.MapGet("/alive", () => Results.Ok("Alive"))
    .AllowAnonymous()
    .RequireRateLimiting("HealthMemory");

app.MapGet("/", () => "TaskFlow Gateway")
    .AllowAnonymous();

app.MapAuthModeEndpoint(authMode);
app.MapReverseProxy().RequireAuthorization();

app.Run();
