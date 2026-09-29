using EF.AspNetCore.Correlation;
using EF.AspNetCore.Proxy;
using EF.AspNetCore.Security;
using EF.AspNetCore.Versioning;
using EF.FlowEngine.AdminApi;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using TaskFlow.Api.Endpoints;
using TaskFlow.Api.Endpoints.Cqrs;
using TaskFlow.Api.Grpc;
using TaskFlow.Application.Contracts;
using TaskFlow.Bootstrapper;

namespace TaskFlow.Api;

/// <summary>
/// Owns the HTTP pipeline and route graph for the API host. Service registration stays in
/// RegisterApiServices and Bootstrapper; this type controls middleware order and endpoint shape.
/// </summary>
public static class WebApplicationBuilderExtensions
{
    /// <summary>
    /// Builds the middleware pipeline in dependency order: security, correlation, exception
    /// handling, CORS, auth, rate limiting, docs, health, domain endpoints, then FlowEngine admin.
    /// </summary>
    public static WebApplication ConfigurePipeline(this WebApplication app)
    {
        // Azure App Configuration refresh (D-042) runs in the background (EF.Host), not as middleware.

        // 1. Public scheme/host/path base from the explicitly trusted deployment proxy.
        app.UseProxyForwarding();

        // 2. Security headers
        app.UseBasicSecurityHeaders();

        // 3. Correlation tracking
        app.UseCorrelationId();

        // 4. Exception handler (before routing)
        app.UseExceptionHandler();

        // 5. CORS
        app.UseCors("TaskFlowUi");

        // 6. Authentication
        app.UseAuthentication();

        // 7. Authorization
        app.UseAuthorization();

        // 8. Rate limiter. After authentication, because the tenant budgets partition on the caller's
        // tenant_id claim: ahead of it the user is anonymous, every request falls back to the remote address,
        // and behind the gateway all tenants share one bucket. Routing already ran implicitly, so endpoint
        // policies (Export, health) still apply.
        app.UseRateLimiter();

        // 9. Request timeouts (D-064): after routing/auth so endpoint metadata (DisableRequestTimeout on
        // the streaming routes) applies. No explicit UseRouting/UseEndpoints call exists in this minimal
        // API pipeline, so routing already ran implicitly before the first middleware above.
        app.UseRequestTimeouts();

        // OpenAPI / Scalar
        if (app.Configuration.GetValue<bool>("OpenApiSettings:Enable", true))
        {
            // .WithDocumentPerVersion() is required by EF.AspNetCore AddEfVersionedOpenApi: the
            // documents now come from AddApiVersioning().AddOpenApi(), and a plain MapOpenApi() serves none
            // of them (every document URL 404s). Document names are unchanged. 1.1.101 also restored the
            // per-document group-name ShouldInclude predicate, so version-neutral routes (/alive, the
            // FlowEngine admin group) are excluded from every versioned document without app-side opt-outs.
            app.MapOpenApi()
                .WithDocumentPerVersion()
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
        // These routes carry no API version and are therefore excluded from every versioned document by
        // EF.AspNetCore's group-name predicate; the published v1 contract is the domain API, not the
        // admin surface.
        app.MapFlowEngineAdmin(prefix: "/api/flowengine");

        return app;
    }

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
        // The tenant budget is the global limiter (RegisterApiServices.AddRateLimiting); a group policy over
        // the same Redis key would spend two permits per request.
        var api = app.MapVersionedApiGroup(ApiContract.VersionedRoutePrefix, versionSet, ApiContract.DefaultVersion);

        var style = ApplicationStyleResolver.Resolve(app.Configuration[ApplicationStyleResolver.ConfigKey]);
        if (style == ApplicationStyle.Cqrs)
        {
            api.MapCategoryCqrsEndpoints();
            api.MapTagCqrsEndpoints();
            api.MapTaskItemCqrsEndpoints();
            api.MapCommentCqrsEndpoints();
            api.MapChecklistItemCqrsEndpoints();
            api.MapAttachmentCqrsEndpoints();
        }
        else
        {
            api.MapCategoryEndpoints();
            api.MapTagEndpoints();
            api.MapTaskItemEndpoints();
            api.MapCommentEndpoints();
            api.MapChecklistItemEndpoints();
            api.MapAttachmentEndpoints();
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
