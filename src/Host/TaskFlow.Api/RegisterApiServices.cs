using EF.AspNetCore.Concurrency;
using EF.AspNetCore.Cors;
using EF.AspNetCore.ExceptionHandling;
using EF.Common.Exceptions;
using EF.Data.Contracts;
using EF.AspNetCore.Versioning;
using EF.Grpc;
using EF.Auth.Relay;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using TaskFlow.Api.Auth;
using TaskFlow.Api.Serialization;
using TaskFlow.Api.Endpoints;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Models.Serialization;
using TaskFlow.Infrastructure.Caching;
using TaskFlow.Infrastructure.Caching.RateLimiting;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Api;

/// <summary>
/// API-only service registration. The Bootstrapper owns business and infrastructure services;
/// this type owns HTTP concerns such as JSON, CORS, auth, ProblemDetails, rate limiting, and OpenAPI.
/// </summary>
public static class RegisterApiServices
{
    /// <summary>
    /// Adds HTTP-facing dependencies without building the app.
    /// </summary>
    public static IServiceCollection AddApiServices(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpContextAccessor();
        // Streaming instruments: a streamed export has no meaningful ASP.NET request duration, so the export
        // endpoint records its own row count and elapsed time.
        services.AddSingleton<StreamingMeter>();
        AddJsonOptions(services);
        // Origins validated at registration: none, a trailing '/', a path, or '*' with credentials fails startup.
        services.AddCorsPolicyFromConfiguration("TaskFlowUi", config.GetSection("Cors"));
        AddAuthentication(services, config);
        AddAuthorization(services);
        AddExceptionHandling(services);
        AddRateLimiting(services, config);
        AddRequestTimeouts(services, config);
        AddVersionedOpenApi(services, config);

        // D-054: the internal gRPC read service. Nothing else changes here - it shares this host's
        // authentication, authorization, request context and exception taxonomy; only the transport is
        // different. EF.Grpc's ServiceErrorInterceptor translates through the same ExceptionClassifier the
        // HTTP handler uses. The Status detail it sends is the category name - exception text stays in the
        // server log instead of the wire, which is why IncludeExceptionMessageInResponse is left at its
        // (false) default.
        services.AddGrpc(options => options.Interceptors.Add<ServiceErrorInterceptor>());

        // Workflow JSON seeding is now configured in the bootstrapper via
        // FlowEngineBuilder.AddWorkflowJsonSeeding.
        // The seeding hosted service auto-discovers ./Workflows at startup.

        return services;
    }

    /// <summary>Registers JSON options dependencies in the service container.</summary>
    private static void AddJsonOptions(IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

            // D-048: TaskFlow's own shapes resolve through generated metadata; ProblemDetails and the
            // third-party shapes behind it keep the reflection resolver that ConfigureHttpJsonOptions
            // already installed, which stays LAST in the chain. Insert(0) rather than assigning
            // TypeInfoResolver, which would replace the chain and break every unregistered type.
            // The naming policy still comes from these options (Web defaults), not from the contexts,
            // so the JSON on the wire is byte-identical to the reflection-serialized output.
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, TaskFlowJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(1, TaskFlowApiJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(2, TaskFlowMessagingJsonContext.Default);
        });
    }

    /// <summary>
    /// Registers authentication: the Scaffold fixed principal, then the EF.Auth trusted-gateway claims relay bound
    /// from the same <c>ForwardedClaims</c> section the Gateway binds. The relay replaces the principal only for an
    /// app-only token from a caller listed in <c>ForwardedClaims:TrustedCallerIds</c> (empty here, so it is inert:
    /// the Scaffold principal carries no caller id), and the relayed identity holds only the relayed claims.
    /// </summary>
    private static void AddAuthentication(IServiceCollection services, IConfiguration config)
    {
        services.AddTaskFlowAuth(config);
        services.AddForwardedClaimsTransformation(config);
    }

    /// <summary>Registers authorization dependencies in the service container.</summary>
    private static void AddAuthorization(IServiceCollection services)
    {
        services.AddTaskFlowAuthorization();
    }

    /// <summary>
    /// Registers the EF.AspNetCore problem-details contract (status from <see cref="ExceptionClassifier"/>,
    /// requestId/traceId/spanId on every problem, no exception text on a 5xx outside Development) with
    /// TaskFlow's mappings added.
    /// </summary>
    private static void AddExceptionHandling(IServiceCollection services)
    {
        services.AddEfProblemDetails();
        services.AddExceptionClassifier(MapExceptions);
    }

    /// <summary>
    /// TaskFlow's additions to the one exception taxonomy the HTTP handler and the gRPC interceptor share
    /// (the classifier already maps the EF.Common.Contracts exceptions - PreconditionFailedException for a stale
    /// If-Match, ConflictException for a conflicting idempotent create (D-033) - plus KeyNotFound,
    /// UnauthorizedAccess, Timeout and cancellation):
    /// <list type="bullet">
    /// <item>A policy-free save's DbUpdateConcurrencyException is a lost update: 412 / FailedPrecondition.</item>
    /// <item>Caller input TaskFlow rejects: <see cref="InvalidRequestException"/> (page size out of range) and the
    /// cursor codec's <see cref="InvalidCursorException"/> (tampered, foreign-tenant, other-sort-mode or stale-schema
    /// cursor and continuation tokens): 400 / InvalidArgument. EF.Common's ValidationException is already
    /// Validation, and BadHttpRequestException keeps its own status.</item>
    /// </list>
    /// Framework ArgumentException, FormatException and InvalidOperationException stay unmapped (500 / Internal):
    /// thrown outside TaskFlow's input checks they are server bugs, and a 4xx would hide them and echo their text.
    /// </summary>
    internal static void MapExceptions(ExceptionClassifierOptions options) => options
        .Map<DbUpdateConcurrencyException>(ExceptionCategory.PreconditionFailed)
        .Map<InvalidRequestException>(ExceptionCategory.Validation)
        .Map<InvalidCursorException>(ExceptionCategory.Validation);

    /// <summary>Registers rate limiting dependencies in the service container.</summary>
    private static void AddRateLimiting(IServiceCollection services, IConfiguration config)
    {
        var healthMemoryPermitLimit = config.GetValue<int?>("RateLimiting:Health:MemoryPermitLimit") ?? 30;
        var healthDbPermitLimit = config.GetValue<int?>("RateLimiting:Health:DbPermitLimit") ?? 6;
        var healthFullPermitLimit = config.GetValue<int?>("RateLimiting:Health:FullPermitLimit") ?? 3;

        services.AddTaskFlowRateLimiting(config);

        services.AddRateLimiter(options =>
        {
            // Tenant budgets live in Redis so they are one allowance across replicas rather than one per
            // replica; the health partitions stay in process because they exist to protect this instance's
            // probes and must keep working when Redis does not. The /healthz probes carry DisableRateLimiting
            // (MapEfHealthEndpoints), which skips this global limiter too.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.Request.Path.StartsWithSegments("/health")
                    || context.Request.Path.StartsWithSegments("/alive"))
                    return RateLimitPartition.GetNoLimiter("health");

                // The export route counts only against its own Export budget (endpoint policy below); counting
                // it here too would let one long export spend the tenant's interactive allowance.
                if (context.GetEndpoint()?.Metadata.GetMetadata<ExportRateLimitPolicy>() is not null)
                    return RateLimitPartition.GetNoLimiter("export");

                var limiters = context.RequestServices.GetRequiredService<TenantRateLimiterFactory>();
                return RateLimitPartition.Get(TenantPartitionKey(context), limiters.CreateTenantLimiter);
            });

            // The streaming export holds a connection for as long as a tenant has rows, so it gets its own
            // budget instead of draining the tenant's interactive allowance.
            options.AddPolicy(ExportRateLimitPolicy.PolicyName, context =>
            {
                var limiters = context.RequestServices.GetRequiredService<TenantRateLimiterFactory>();
                return RateLimitPartition.Get(TenantPartitionKey(context), limiters.CreateExportLimiter);
            });

            options.AddPolicy("HealthMemory", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = healthMemoryPermitLimit,
                    Window = TimeSpan.FromSeconds(10),
                    QueueLimit = 5,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

            options.AddPolicy("HealthDb", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = healthDbPermitLimit,
                    Window = TimeSpan.FromSeconds(10),
                    QueueLimit = 2,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

            options.AddPolicy("HealthFull", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = healthFullPermitLimit,
                    Window = TimeSpan.FromSeconds(30),
                    QueueLimit = 1,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                var limiters = context.HttpContext.RequestServices.GetRequiredService<TenantRateLimiterFactory>();
                limiters.RecordRejected(context.HttpContext.User?.FindFirst("tenant_id")?.Value);
                return ValueTask.CompletedTask;
            };
        });
    }

    /// <summary>
    /// Registers request-timeout dependencies in the service container (D-064). Only the default policy is
    /// configured here; streaming endpoints (SSE token stream, NDJSON export) opt out at their own mapping
    /// site with DisableRequestTimeout(), and health endpoints are left on the default.
    /// </summary>
    private static void AddRequestTimeouts(IServiceCollection services, IConfiguration config)
    {
        var defaultSeconds = config.GetValue<int?>("RequestTimeouts:DefaultSeconds") ?? 30;
        services.AddRequestTimeouts(options =>
        {
            options.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(defaultSeconds)
            };
        });
    }

    /// <summary>
    /// Partition key for a tenant budget. An unauthenticated caller has no tenant, so it falls back to the
    /// remote address: without that every anonymous caller would share one bucket and a single client could
    /// exhaust the allowance for all of them.
    /// </summary>
    private static string TenantPartitionKey(HttpContext context) =>
        context.User?.FindFirst("tenant_id")?.Value
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";

    /// <summary>Registers versioned open API dependencies in the service container.</summary>
    private static void AddVersionedOpenApi(IServiceCollection services, IConfiguration config)
    {
        services.AddEfVersionedOpenApi(options =>
        {
            options.Title = ApiContract.Title;
            options.Description = ApiContract.Description;
            options.ApiExplorerGroupNameFormat = ApiContract.ApiExplorerGroupNameFormat;
            options.EnableOpenApi = config.GetValue<bool>("OpenApiSettings:Enable", true);

            foreach (var apiDocument in ApiContract.SupportedDocuments)
            {
                options.Documents.Add(new ApiVersionDocument(apiDocument.Version, apiDocument.GroupName)
                {
                    DisplayName = apiDocument.DisplayName
                });
            }
        });

        // Configures every named document AddEfVersionedOpenApi created.
        services.AddConcurrencyOpenApiContract();
    }
}
