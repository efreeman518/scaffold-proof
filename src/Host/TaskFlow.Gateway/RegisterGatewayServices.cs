using EF.AspNetCore.Cors;
using EF.AspNetCore.ExceptionHandling;
using EF.Auth.Fixed;
using EF.Auth.Tokens;
using EF.Gateway;
using EF.Host;
using EF.RateLimiting;
using Microsoft.AspNetCore.Http.Timeouts;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Gateway;

/// <summary>
/// Gateway composition root. It validates external users, forwards original user claims to the
/// API, acquires downstream service tokens, applies CORS, health checks, rate limiting, and YARP.
/// </summary>
public static class RegisterGatewayServices
{
    /// <summary>Configuration section of the downstream Api health probe.</summary>
    public const string ApiHealthSectionName = "AggregateHealthCheck";

    /// <summary>
    /// Registers gateway-only services. The API remains the authorization and business boundary;
    /// the gateway handles edge auth and downstream token exchange.
    /// </summary>
    public static IServiceCollection AddGatewayServices(
        this IServiceCollection services, IConfiguration config)
    {
        // One credential for downstream token exchange, honoring ManagedIdentityClientId / AzureTenantId;
        // AccessTokenCache (EF.Auth) uses it because it is registered first.
        services.AddAzureTokenCredential(config);
        services.AddAccessTokenCache();
        services.AddEfProblemDetails();
        AddAuthentication(services, config);
        AddReverseProxy(services, config);
        // Origins validated at registration; CorsSettings:AllowCredentials (appsettings.json) allows the Uno client's credentials.
        services.AddCorsPolicyFromConfiguration("UnoUI", config.GetSection("CorsSettings"));
        AddHealthChecks(services, config);
        AddRateLimiting(services, config);
        AddRequestTimeouts(services, config);
        return services;
    }

    /// <summary>
    /// Registers request-timeout dependencies in the service container (D-064). The default policy sits
    /// above the Api's own budget so the gateway is never the tighter of the two; the routes that proxy the
    /// Api's streaming endpoints (SSE token stream, NDJSON export) carry their own "Disable" TimeoutPolicy in
    /// ReverseProxy:Routes so a long-held proxied connection is not cut by this default either.
    /// </summary>
    private static void AddRequestTimeouts(IServiceCollection services, IConfiguration config)
    {
        var defaultSeconds = config.GetValue<int?>("RequestTimeouts:DefaultSeconds") ?? 35;
        services.AddRequestTimeouts(options =>
        {
            options.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(defaultSeconds)
            };
        });
    }

    /// <summary>
    /// Registers the EF.Auth fixed-principal scheme with <see cref="ScaffoldPrincipal"/>; the host fails to start
    /// outside <see cref="ScaffoldPrincipal.AllowedEnvironments"/>.
    /// </summary>
    private static void AddAuthentication(IServiceCollection services, IConfiguration config)
    {
        _ = AuthModeResolver.Resolve(config[AuthModeResolver.ConfigKey]);

        services.AddAuthentication(ScaffoldPrincipal.SchemeName)
            .AddFixedPrincipal(ScaffoldPrincipal.SchemeName, options =>
            {
                options.Claims = [.. ScaffoldPrincipal.Claims.Select(c => new FixedClaim(c.Type, c.Value))];
                options.AllowedEnvironments = [.. ScaffoldPrincipal.AllowedEnvironments];
            });
    }

    /// <summary>
    /// YARP with the EF.Gateway downstream auth transforms: every route strips any inbound relay header, a cluster
    /// with <c>Metadata:RelayUserClaims</c> gets the authenticated user's claims in it, and a cluster with
    /// <c>Metadata:TokenScope</c> gets a bearer token from <see cref="AccessTokenCache"/>. The relay options bind the
    /// same <c>ForwardedClaims</c> section the Api binds, so both hosts agree on the header name and allowlist.
    /// </summary>
    private static void AddReverseProxy(IServiceCollection services, IConfiguration config)
    {
        services.AddReverseProxy()
            .LoadFromConfig(config.GetSection("ReverseProxy"))
            .AddServiceDiscoveryDestinationResolver()
            .AddDownstreamAuthTransforms(config);
    }

    /// <summary>
    /// Probes the Api's full health endpoint (EF.Gateway <see cref="DownstreamHealthCheck"/>). A missing or relative
    /// <c>AggregateHealthCheck:TaskFlowApiHealthUrl</c> fails startup; <c>TokenScope</c> is empty in Scaffold mode.
    /// </summary>
    private static void AddHealthChecks(IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(ApiHealthSectionName);
        services.AddHealthChecks()
            .AddDownstreamHealthCheck("taskflow-api", options =>
            {
                options.Url = Uri.TryCreate(section["TaskFlowApiHealthUrl"], UriKind.Absolute, out var url) ? url : null;
                options.TokenScope = section["TokenScope"];
                options.TimeoutSeconds = section.GetValue("TimeoutSeconds", options.TimeoutSeconds);
            }, tags: ["full", "extservice"]);
    }

    /// <summary>
    /// D-050 edge limiter (EF.RateLimiting <c>UseEdgeLimiter</c>, section <c>RateLimiting:Edge</c>): a token bucket per
    /// client IP chained with one process-wide concurrency limiter, 429 with Retry-After. The client IP is the real one
    /// only because <c>UseProxyForwarding</c> runs before <c>UseRateLimiter</c> in <c>Program.cs</c>. Health and probe
    /// paths skip the bucket but stay inside the concurrency backstop; the per-IP health policies bound them.
    /// An out-of-range budget throws when the rate limiter middleware first reads the options, at host start.
    /// </summary>
    private static void AddRateLimiting(IServiceCollection services, IConfiguration config)
    {
        var memoryPermitLimit = config.GetValue<int?>("RateLimiting:Health:MemoryPermitLimit") ?? 30;
        var fullPermitLimit = config.GetValue<int?>("RateLimiting:Health:FullPermitLimit") ?? 3;
        var edge = config.GetSection(EdgeRateLimitSettings.ConfigSectionName).Get<EdgeRateLimitSettings>()
            ?? new EdgeRateLimitSettings();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.UseEdgeLimiter(edge);
            options.AddPerClientIpFixedWindowPolicy("HealthMemory", memoryPermitLimit, TimeSpan.FromSeconds(10), queueLimit: 5);
            options.AddPerClientIpFixedWindowPolicy("HealthFull", fullPermitLimit, TimeSpan.FromSeconds(30), queueLimit: 1);
        });
    }
}
