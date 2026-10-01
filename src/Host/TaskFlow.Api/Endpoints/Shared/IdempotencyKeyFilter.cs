using EF.AspNetCore;
using EF.Common.Contracts;
using System.Globalization;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Api.Endpoints.Shared;

/// <summary>
/// D-074: an <c>Idempotency-Key</c> header on a create or child add, with no body id, is mapped to a stored UUIDv7
/// before the handler runs, and the body id is set to it. A retried, concurrent or recovered request with the same
/// key therefore sends the same id, and the existing replay paths (GR-17, D-073) return the stored row, or 409 for
/// a divergent payload. A body id the caller supplied wins and the header is ignored.
/// </summary>
internal static class IdempotencyKeyFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const int KeyMaxLength = 200;

    public const string TaskItemCreateScope = "task-item.create";

    /// <summary>
    /// Child-add scopes carry the root id, so one key used on two tasks maps to two different children. The id is the
    /// parsed route value in "D" format, so the same task named in another accepted format is the same scope.
    /// </summary>
    public static string TaskItemChildScope(HttpContext httpContext, string child) =>
        $"task-item.{child}.add:{RouteTaskItemId(httpContext).ToString("D", CultureInfo.InvariantCulture)}";

    /// <summary>The <c>{id:guid}</c> route value; the route constraint has already accepted it as a Guid.</summary>
    private static Guid RouteTaskItemId(HttpContext httpContext) =>
        Guid.Parse(Convert.ToString(httpContext.GetRouteValue("id"), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    /// <summary>
    /// Applies the mapping to a route whose body is a <see cref="DefaultRequest{T}"/> of <typeparamref name="TDto"/>.
    /// The routes already declare their 400 (ProducesValidationProblem), so the OpenAPI document is unchanged.
    /// </summary>
    public static RouteHandlerBuilder WithIdempotencyKey<TDto>(this RouteHandlerBuilder builder, Func<HttpContext, string> scope)
        where TDto : EntityBaseDto =>
        builder
            .AddEndpointFilter(async (context, next) =>
            {
                var item = context.Arguments.OfType<DefaultRequest<TDto>>().SingleOrDefault()?.Item;
                var headers = context.HttpContext.Request.Headers[HeaderName];
                if (item is null || item.Id is not null || headers.Count == 0) return await next(context);

                if (headers.Count > 1 || string.IsNullOrWhiteSpace(headers[0]) || headers[0]!.Length > KeyMaxLength)
                    return TypedResults.Problem(ProblemDetailsHelper.Create(
                        StatusCodes.Status400BadRequest,
                        string.Format(CultureInfo.InvariantCulture, ErrorConstants.ERROR_IDEMPOTENCY_KEY_INVALID, KeyMaxLength)));

                var services = context.HttpContext.RequestServices;
                var tenantId = services.GetRequiredService<IRequestContext<string, Guid?>>().TenantId ?? Guid.Empty;
                item.Id = await services.GetRequiredService<IIdempotencyKeyRepository>().GetOrAddEntityIdAsync(
                    tenantId, scope(context.HttpContext), headers[0]!, context.HttpContext.RequestAborted);
                return await next(context);
            });
}
