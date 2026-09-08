using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using TaskFlow.Application.Contracts;
using TaskFlow.Gateway;

var builder = WebApplication.CreateBuilder(args);

// Azure App Configuration (D-042): dynamic config, no-op unless AppConfig:Endpoint (or
// ConnectionStrings:AppConfig) is set. Runs first so later configuration reads see values it overrides.
builder.AddGatewayAppConfiguration();

builder.AddServiceDefaults();
builder.AddProxyForwarding();
builder.Services.AddGatewayServices(builder.Configuration);
var authMode = AuthModeResolver.Resolve(builder.Configuration[AuthModeResolver.ConfigKey]);

// Authorization (auth registered in AddGatewayServices)
builder.Services.AddAuthorization();

var app = builder.Build();

// Pipeline order: security -> CORS -> middleware -> endpoints -> reverse proxy
// Azure App Configuration sentinel-key refresh middleware (D-042), guarded the same way the config
// source itself was added - registering it without the provider having been added throws.
if (GatewayAppConfiguration.IsAppConfigurationConfigured(app.Configuration))
{
    app.UseAzureAppConfiguration();
}

// Adopt the edge proxy's public scheme/host before auth and before YARP re-stamps
// X-Forwarded-* for the downstream app.
app.UseProxyForwarding();
app.UseExceptionHandler(appBuilder =>
    appBuilder.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
    }));

app.UseCors("UnoUI");
app.UseHeaderPropagation();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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
