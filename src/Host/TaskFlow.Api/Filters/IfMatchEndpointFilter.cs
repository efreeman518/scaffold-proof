using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Api.Filters;

/// <summary>
/// Enforces the If-Match precondition on mutating routes (GR-16). 428 is an HTTP-level concern and
/// lives here; 412 is an application-level concern and comes from ConcurrencyGuard - keeping them in
/// separate layers is what lets the same guard serve both application styles unchanged.
/// </summary>
internal sealed class IfMatchEndpointFilter(ILogger<IfMatchEndpointFilter> logger) : IEndpointFilter
{
    /// <summary>Rejects the request when the If-Match header is absent or unusable.</summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var header = httpContext.Request.Headers.IfMatch;

        if (header.Count == 0 || string.IsNullOrWhiteSpace(header[0]))
        {
            return Results.Problem(
                title: "Precondition required",
                detail: ErrorConstants.ERROR_IF_MATCH_REQUIRED,
                statusCode: StatusCodes.Status428PreconditionRequired,
                instance: $"{httpContext.Request.Method} {httpContext.Request.Path}");
        }

        if (!IfMatch.TryParse(header, out var ifMatch))
        {
            return Results.Problem(
                title: "Bad request",
                detail: ErrorConstants.ERROR_IF_MATCH_MALFORMED,
                statusCode: StatusCodes.Status400BadRequest,
                instance: $"{httpContext.Request.Method} {httpContext.Request.Path}");
        }

        // The wildcard skips the version check entirely, so it is logged: it is the one way a caller
        // can overwrite a concurrent change on purpose, and an unexplained spike in it is a bug
        // somewhere upstream (a client that stopped tracking ETags), not normal traffic.
        if (ifMatch.IsWildcard)
        {
            logger.LogInformation(
                "If-Match wildcard override on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        return await next(context);
    }
}

/// <summary>
/// Marker metadata for routes that require If-Match. Read by the OpenAPI transformer to declare the
/// header and the 412/428 responses, and by the architecture test that proves no mutating non-POST
/// route was added without the precondition.
/// </summary>
public sealed class IfMatchRequiredMetadata;

/// <summary>Route-builder helpers for the concurrency contract.</summary>
public static class ConcurrencyEndpointExtensions
{
    /// <summary>Adds the If-Match filter, its failure responses, and the metadata the OpenAPI transformer reads.</summary>
    public static RouteHandlerBuilder RequireIfMatch(this RouteHandlerBuilder builder) =>
        builder
            .AddEndpointFilter<IfMatchEndpointFilter>()
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithMetadata(new IfMatchRequiredMetadata());
}
