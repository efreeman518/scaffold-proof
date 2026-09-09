using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using TaskFlow.Gateway.HealthChecks;
using TaskFlow.Application.Contracts;
using Yarp.ReverseProxy.Transforms;

namespace TaskFlow.Gateway;

/// <summary>
/// Gateway composition root. It validates external users, forwards original user claims to the
/// API, acquires downstream service tokens, applies CORS, health checks, rate limiting, and YARP.
/// </summary>
public static class RegisterGatewayServices
{
    private const string OriginalUserClaimsHeaderName = "X-Orig-Request";

    /// <summary>
    /// Registers gateway-only services. The API remains the authorization and business boundary;
    /// the gateway handles edge auth and downstream token exchange.
    /// </summary>
    public static IServiceCollection AddGatewayServices(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton<TokenService>();
        services.AddHeaderPropagation(options => options.Headers.Add("X-Correlation-Id"));
        AddAuthentication(services, config);
        AddReverseProxy(services, config);
        AddCors(services, config);
        AddHealthChecks(services, config);
        AddRateLimiting(services, config);
        return services;
    }

    /// <summary>Registers authentication dependencies in the service container.</summary>
    private static void AddAuthentication(IServiceCollection services, IConfiguration config)
    {
        _ = AuthModeResolver.Resolve(config[AuthModeResolver.ConfigKey]);

        services.AddAuthentication(ScaffoldAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ScaffoldAuthHandler>(
                ScaffoldAuthHandler.SchemeName, _ => { });
    }

    /// <summary>Registers reverse proxy dependencies in the service container.</summary>
    private static void AddReverseProxy(IServiceCollection services, IConfiguration config)
    {
        services.AddReverseProxy()
            .LoadFromConfig(config.GetSection("ReverseProxy"))
            .AddServiceDiscoveryDestinationResolver()
            .AddTransforms(context =>
            {
                context.AddRequestTransform(async ctx =>
                {
                    var tokenService = ctx.HttpContext.RequestServices.GetRequiredService<TokenService>();
                    var clusterId = context.Cluster?.ClusterId ?? "api-cluster";

                    // Forward original user claims as X-Orig-Request header for the API
                    // claims transformer after the API validates the gateway service token.
                    AddOriginalUserClaimsHeader(ctx);

                    // Acquire service token for downstream API
                    var token = await tokenService.GetAccessTokenAsync(clusterId, ctx.HttpContext.RequestAborted);
                    ctx.ProxyRequest!.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                });
            });
    }

    /// <summary>Registers original user claims header dependencies in the service container.</summary>
    private static void AddOriginalUserClaimsHeader(RequestTransformContext ctx)
    {
        ReplaceOriginalUserClaimsHeader(ctx.ProxyRequest!, ctx.HttpContext.User);
    }

    /// <summary>Removes untrusted inbound claim data before writing the authenticated gateway identity.</summary>
    internal static void ReplaceOriginalUserClaimsHeader(
        HttpRequestMessage proxyRequest,
        ClaimsPrincipal user)
    {
        proxyRequest.Headers.Remove(OriginalUserClaimsHeaderName);
        if (user.Identity?.IsAuthenticated != true) return;

        var claimsPayload = new
        {
            sub = user.FindFirst("oid")?.Value
               ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? user.FindFirst("sub")?.Value,
            tenant_id = user.FindFirst("tenant_id")?.Value,
            name = user.FindFirst(ClaimTypes.Name)?.Value,
            roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray()
        };

        var json = JsonSerializer.Serialize(claimsPayload);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        proxyRequest.Headers.TryAddWithoutValidation(OriginalUserClaimsHeaderName, encoded);
    }

    /// <summary>Registers cors dependencies in the service container.</summary>
    private static void AddCors(IServiceCollection services, IConfiguration config)
    {
        var origins = config.GetSection("CorsSettings:AllowedOrigins").Get<string[]>();
        if (origins is null || origins.Length == 0)
        {
            throw new InvalidOperationException("CORS is not configured. Set CorsSettings:AllowedOrigins in configuration.");
        }

        services.AddCors(options =>
        {
            options.AddPolicy("UnoUI", policy =>
            {
                policy.WithOrigins(origins)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials();
            });
        });
    }

    /// <summary>Registers health checks dependencies in the service container.</summary>
    private static void AddHealthChecks(IServiceCollection services, IConfiguration config)
    {
        services.Configure<AggregateHealthCheckSettings>(
            config.GetSection(AggregateHealthCheckSettings.ConfigSectionName));

        services.AddHttpClient(nameof(AggregateGatewayHealthCheck));

        services.AddHealthChecks()
            .AddCheck<AggregateGatewayHealthCheck>("taskflow-api", tags: ["full", "extservice"]);
    }

    /// <summary>Registers rate limiting dependencies in the service container.</summary>
    private static void AddRateLimiting(IServiceCollection services, IConfiguration config)
    {
        var memoryPermitLimit = config.GetValue<int?>("RateLimiting:Health:MemoryPermitLimit") ?? 30;
        var fullPermitLimit = config.GetValue<int?>("RateLimiting:Health:FullPermitLimit") ?? 3;
        var edge = config.GetSection(EdgeRateLimitSettings.ConfigSectionName).Get<EdgeRateLimitSettings>()
            ?? new EdgeRateLimitSettings();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            if (edge.Enabled)
            {
                options.GlobalLimiter = BuildEdgeLimiter(edge);
                options.OnRejected = WriteRetryAfter(edge);
            }

            options.AddPolicy("HealthMemory", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = memoryPermitLimit,
                        Window = TimeSpan.FromSeconds(10),
                        QueueLimit = 5,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            options.AddPolicy("HealthFull", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = fullPermitLimit,
                        Window = TimeSpan.FromSeconds(30),
                        QueueLimit = 1,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
        });
    }

    /// <summary>
    /// The global limiter for proxied traffic (D-050): a token bucket per client IP chained with one process-wide
    /// concurrency limiter. Two limiters because they answer different questions - the bucket caps how fast one
    /// caller may arrive, the concurrency limiter caps how many requests this replica may have in flight when the
    /// downstream slows down, which no per-caller budget can bound.
    /// <para>
    /// The client IP comes from <c>Connection.RemoteIpAddress</c>, which is the real client only because
    /// <c>UseProxyForwarding</c> runs before <c>UseRateLimiter</c> in <c>Program.cs</c> and rewrites it from the
    /// trusted <c>X-Forwarded-For</c> chain. Moving the limiter above that middleware would silently partition
    /// every request into the edge proxy's single address.
    /// </para>
    /// Health and liveness routes get no limiter: they exist to report this instance's state, and shedding a
    /// probe is how a healthy replica gets restarted or pulled out of rotation.
    /// </summary>
    private static PartitionedRateLimiter<HttpContext> BuildEdgeLimiter(EdgeRateLimitSettings edge) =>
        PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (IsProbe(context.Request.Path))
                    return RateLimitPartition.GetNoLimiter("probe");

                return RateLimitPartition.GetTokenBucketLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = edge.TokensPerPeriod,
                        TokensPerPeriod = edge.TokensPerPeriod,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(Math.Max(1, edge.ReplenishmentSeconds)),
                        QueueLimit = edge.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            }),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsProbe(context.Request.Path)
                    ? RateLimitPartition.GetNoLimiter("probe")
                    : RateLimitPartition.GetConcurrencyLimiter(
                        "edge",
                        _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = edge.MaxConcurrentRequests,
                            QueueLimit = edge.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                        })));

    /// <summary>
    /// Adds Retry-After to the 429 so a client backs off by the limiter's own replenishment rather than
    /// guessing. The token bucket reports the wait when it knows it; the period is the floor otherwise.
    /// </summary>
    private static Func<OnRejectedContext, CancellationToken, ValueTask> WriteRetryAfter(EdgeRateLimitSettings edge) =>
        (context, _) =>
        {
            var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                ? wait
                : TimeSpan.FromSeconds(Math.Max(1, edge.ReplenishmentSeconds));

            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

            return ValueTask.CompletedTask;
        };

    private static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/healthz")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/alive");
}
