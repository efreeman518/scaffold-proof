using EF.AspNetCore.Correlation;
using EF.AspNetCore.Security;
using EF.AspNetCore.Versioning;
using EF.FlowEngine.AdminApi;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using TaskFlow.Api.Endpoints;
using TaskFlow.Api.Endpoints.Cqrs;
using TaskFlow.Api.Grpc;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Api;

/// <summary>
/// Owns the HTTP pipeline and route graph for the API host. Service registration stays in
/// RegisterApiServices and Bootstrapper; this type controls middleware order and endpoint shape.
/// </summary>
public static class WebApplicationBuilderExtensions
{
    private static bool _problemDetailsIncludeStackTrace;

    /// <summary>
    /// Builds the middleware pipeline in dependency order: security, correlation, exception
    /// handling, rate limiting, CORS, auth, docs, health, domain endpoints, then FlowEngine admin.
    /// </summary>
    public static WebApplication ConfigurePipeline(this WebApplication app)
    {
        _problemDetailsIncludeStackTrace = app.Environment.IsDevelopment() || app.Environment.IsStaging();

        // 1. Public scheme/host/path base from the explicitly trusted deployment proxy.
        app.UseProxyForwarding();

        // 2. Security headers
        app.UseBasicSecurityHeaders();

        // 3. Correlation tracking
        app.UseCorrelationId();
        app.UseHeaderPropagation();

        // 4. Exception handler (before routing)
        app.UseExceptionHandler();

        // 5. Rate limiter
        app.UseRateLimiter();

        // 6. CORS
        app.UseCors("TaskFlowUi");

        // 7. Authentication
        app.UseAuthentication();

        // 8. Authorization
        app.UseAuthorization();

        // OpenAPI / Scalar
        if (app.Configuration.GetValue<bool>("OpenApiSettings:Enable", true))
        {
            app.MapOpenApi()
                .AllowAnonymous();
            app.MapScalarApiReference(options =>
            {
                options.WithTitle(ApiContract.Title);
                options.WithTheme(ScalarTheme.Moon);
            })
            .AllowAnonymous();
        }

        // Default Aspire endpoints
        app.MapDefaultEndpoints();

        // Health endpoints
        app.MapHealthChecks("/health/memory", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("memory")
        })
        .AllowAnonymous()
        .RequireRateLimiting("HealthMemory");

        app.MapHealthChecks("/health/db", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("db")
        })
        .RequireAuthorization()
        .RequireRateLimiting("HealthDb");

        app.MapHealthChecks("/health/full", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("full")
        })
        .RequireAuthorization()
        .RequireRateLimiting("HealthFull");

        app.MapGet("/alive", () => Results.Ok("Alive"))
            .AllowAnonymous()
            .RequireRateLimiting("HealthMemory");

        // D-054 internal gRPC read service, served on the dedicated cleartext HTTP/2 Kestrel endpoint
        // (Kestrel:Endpoints:Grpc). RequireAuthorization is redundant with the authenticated-user
        // fallback policy and stated anyway: an unauthenticated internal RPC surface is not something a
        // reader should have to infer from a policy declared in another file.
        app.MapGrpcService<TaskFlowReadGrpcService>()
            .RequireAuthorization();

        // API endpoint groups
        SetupApiEndpoints(app);

        // FlowEngine admin API - instance/registry/circuit-breaker/human-task operations.
        // Fronted by YARP gateway; consumed by EF.FlowEngine.Dashboard hosted in TaskFlow.Blazor.
        app.MapFlowEngineAdmin(prefix: "/api/flowengine");

        return app;
    }

    public static bool ProblemDetailsIncludeStackTrace => _problemDetailsIncludeStackTrace;

    /// <summary>
    /// Maps the public API contract once, then routes entity CRUD to either application services
    /// or CQRS handlers based on Application:Style / TASKFLOW_APPLICATION_STYLE.
    /// Search, agent, and TaskView endpoints remain shared because they are not style-specific.
    /// </summary>
    private static void SetupApiEndpoints(WebApplication app)
    {
        var apiDocuments = ApiContract.SupportedDocuments
            .Select(apiDocument => new ApiVersionDocument(apiDocument.Version, apiDocument.GroupName)
            {
                DisplayName = apiDocument.DisplayName
            })
            .ToArray();

        var versionSet = app.BuildApiVersionSet(apiDocuments);
        var api = app.MapVersionedApiGroup(ApiContract.VersionedRoutePrefix, versionSet, ApiContract.DefaultVersion)
            .RequireRateLimiting("PerTenant");

        var style = ApplicationStyleResolver.Resolve(app.Configuration[ApplicationStyleResolver.ConfigKey]);
        if (style == ApplicationStyle.Cqrs)
        {
            api.MapCategoryCqrsEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapTagCqrsEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapTaskItemCqrsEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapCommentCqrsEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapChecklistItemCqrsEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapAttachmentCqrsEndpoints(ProblemDetailsIncludeStackTrace);
        }
        else
        {
            api.MapCategoryEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapTagEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapTaskItemEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapCommentEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapChecklistItemEndpoints(ProblemDetailsIncludeStackTrace);
            api.MapAttachmentEndpoints(ProblemDetailsIncludeStackTrace);
        }

        // Style-agnostic reads (summary, metadata, export) - one registration for both styles.
        api.MapTaskFlowReadEndpoints();
        api.MapSearchEndpoints();
        api.MapAgentEndpoints();
        api.MapAiDemoEndpoints();
        api.MapTaskViewEndpoints();
        api.MapOutboxAdminEndpoints();
    }
}
