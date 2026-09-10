using EF.AspNetCore.Correlation;
using EF.AspNetCore.ProblemDetails;
using EF.AspNetCore.Versioning;
using EF.Grpc;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using TaskFlow.Api.Auth;
using TaskFlow.Api.Grpc;
using TaskFlow.Api.Serialization;
using TaskFlow.Api.Middleware;
using TaskFlow.Api.Endpoints;
using TaskFlow.Api.OpenApi;
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
    /// Adds HTTP-facing dependencies without building the app. Startup logging is passed in so
    /// auth and config failures can be reported before the runtime logger factory exists.
    /// </summary>
    public static IServiceCollection AddApiServices(
        this IServiceCollection services, IConfiguration config, ILogger startupLogger)
    {
        services.AddHttpContextAccessor();
        // Streaming instruments: a streamed export has no meaningful ASP.NET request duration, so the export
        // endpoint records its own row count and elapsed time.
        services.AddSingleton<StreamingMeter>();
        AddJsonOptions(services);
        AddCors(services, config);
        AddAuthentication(services, config, startupLogger);
        AddAuthorization(services);
        AddExceptionHandling(services);
        services.AddCorrelationHeaderPropagation();
        AddRateLimiting(services, config);
        AddVersionedOpenApi(services, config);

        // D-054: the internal gRPC read service. Nothing else changes here - it shares this host's
        // authentication, authorization, and request context; only the transport is different.
        // EF.Grpc's ServiceErrorInterceptor does the exception-to-status translation (package request 29):
        // StatusCodeMapper is TaskFlow's own mapping, and the Status detail it sends is a generic
        // "Internal error" - exception text stays in the server log instead of the wire-visible trailer,
        // which is why IncludeLogDataInResponse is left at its (false) default.
        services.Configure<ErrorInterceptorSettings>(settings =>
            settings.StatusCodeMapper = TaskFlowReadGrpcService.StatusFor);
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

    /// <summary>Registers cors dependencies in the service container.</summary>
    private static void AddCors(IServiceCollection services, IConfiguration config)
    {
        var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if (allowedOrigins is null || allowedOrigins.Length == 0)
        {
            throw new InvalidOperationException("CORS is not configured. Set Cors:AllowedOrigins in configuration.");
        }

        services.AddCors(options =>
        {
            options.AddPolicy("TaskFlowUi", policy =>
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod());
        });
    }

    /// <summary>Registers authentication dependencies in the service container.</summary>
    private static void AddAuthentication(IServiceCollection services, IConfiguration config, ILogger logger)
    {
        services.AddTaskFlowAuth(config);
        services.Configure<GatewayClaimsTransformSettings>(
            config.GetSection(GatewayClaimsTransformSettings.ConfigSectionName));
        services.AddTransient<IClaimsTransformation, GatewayClaimsTransformer>();
    }

    /// <summary>Registers authorization dependencies in the service container.</summary>
    private static void AddAuthorization(IServiceCollection services)
    {
        services.AddTaskFlowAuthorization();
    }

    /// <summary>Registers exception handling dependencies in the service container.</summary>
    private static void AddExceptionHandling(IServiceCollection services)
    {
        services.AddExceptionHandler<DefaultExceptionHandler>();
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
                ProblemDetailsMetadata.ApplyRequestMetadata(context.ProblemDetails, context.HttpContext);
        });
    }

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
            // probes and must keep working when Redis does not.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.Request.Path.StartsWithSegments("/health")
                    || context.Request.Path.StartsWithSegments("/alive")
                    || context.Request.Path.StartsWithSegments("/healthz"))
                    return RateLimitPartition.GetNoLimiter("health");

                var limiters = context.RequestServices.GetRequiredService<TenantRateLimiterFactory>();
                return RateLimitPartition.Get(TenantPartitionKey(context), limiters.CreateTenantLimiter);
            });

            options.AddPolicy("PerTenant", context =>
            {
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

        // The versioned OpenAPI helper owns AddOpenApi per document, so the concurrency transformer is
        // attached to the same named options rather than by re-registering the document.
        foreach (var apiDocument in ApiContract.SupportedDocuments)
        {
            services.Configure<Microsoft.AspNetCore.OpenApi.OpenApiOptions>(
                apiDocument.GroupName, options => options.AddOperationTransformer<ConcurrencyOperationTransformer>());
        }
    }
}
