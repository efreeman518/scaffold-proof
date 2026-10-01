using EF.AspNetCore;
using EF.Common.Contracts;
using Microsoft.OpenApi;
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
/// a divergent payload. A non-empty body id the caller supplied wins and the header is ignored.
/// </summary>
internal static class IdempotencyKeyFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const int KeyMaxLength = 200;

    public const string TaskItemCreateScope = "task-item.create";
    public const string CategoryCreateScope = "category.create";
    public const string TagCreateScope = "tag.create";
    public const string AttachmentCreateScope = "attachment.create";

    /// <summary>
    /// Child-add scopes carry the root id, so one key used on two tasks maps to two different children. The id is the
    /// parsed route value in "D" format, so the same task named in another accepted format is the same scope.
    /// </summary>
    public static string TaskItemChildScope(Guid taskItemId, string child) =>
        $"task-item.{child}.add:{taskItemId.ToString("D", CultureInfo.InvariantCulture)}";

    /// <summary>The <c>{id:guid}</c> route value; the route constraint has already accepted it as a Guid.</summary>
    private static Guid RouteTaskItemId(HttpContext httpContext) =>
        Guid.Parse(Convert.ToString(httpContext.GetRouteValue("id"), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    /// <summary>Maps the key of a create whose scope is fixed (<paramref name="scope"/>).</summary>
    public static RouteHandlerBuilder WithIdempotencyKey<TDto>(this RouteHandlerBuilder builder, string scope)
        where TDto : EntityBaseDto =>
        builder.WithKeyMapping<TDto>(async (repository, tenantId, _, key, ct) =>
            await repository.GetOrAddEntityIdAsync(tenantId, scope, key, ct));

    /// <summary>
    /// Maps the key of a child add on the <c>{id}</c> task. The mapping is stored only when that task exists and the
    /// caller's tenant can see it; otherwise nothing is stored and the handler answers its usual 404.
    /// </summary>
    public static RouteHandlerBuilder WithTaskItemChildIdempotencyKey<TDto>(this RouteHandlerBuilder builder, string child)
        where TDto : EntityBaseDto =>
        builder.WithKeyMapping<TDto>((repository, tenantId, httpContext, key, ct) =>
        {
            var taskItemId = RouteTaskItemId(httpContext);
            return repository.GetOrAddChildEntityIdAsync(tenantId, TaskItemChildScope(taskItemId, child), key, taskItemId, ct);
        });

    /// <summary>
    /// Applies the mapping to a route whose body is a <see cref="DefaultRequest{T}"/> of <typeparamref name="TDto"/>, and
    /// documents the optional header on the operation so generated clients can send it. The routes already declare
    /// their 400 (ProducesValidationProblem).
    /// </summary>
    private static RouteHandlerBuilder WithKeyMapping<TDto>(
        this RouteHandlerBuilder builder,
        Func<IIdempotencyKeyRepository, Guid, HttpContext, string, CancellationToken, Task<Guid?>> map)
        where TDto : EntityBaseDto =>
        builder
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = HeaderName,
                    In = ParameterLocation.Header,
                    Required = false,
                    Description = "Optional client key that makes a resent create or add replay its first result instead of writing again. Ignored when the body carries a non-empty id.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = KeyMaxLength }
                });
                return Task.CompletedTask;
            })
            .AddEndpointFilter(async (context, next) =>
            {
                var item = context.Arguments.OfType<DefaultRequest<TDto>>().SingleOrDefault()?.Item;
                var headers = context.HttpContext.Request.Headers[HeaderName];
                // An empty id is no id to the services, so the header maps it the same as a missing one.
                if (item is null || (item.Id is Guid id && id != Guid.Empty) || headers.Count == 0) return await next(context);

                if (headers.Count > 1 || string.IsNullOrWhiteSpace(headers[0]) || headers[0]!.Length > KeyMaxLength)
                    return TypedResults.Problem(ProblemDetailsHelper.Create(
                        StatusCodes.Status400BadRequest,
                        string.Format(CultureInfo.InvariantCulture, ErrorConstants.ERROR_IDEMPOTENCY_KEY_INVALID, KeyMaxLength)));

                var services = context.HttpContext.RequestServices;
                var tenantId = services.GetRequiredService<IRequestContext<string, Guid?>>().TenantId ?? Guid.Empty;
                if (await map(services.GetRequiredService<IIdempotencyKeyRepository>(), tenantId, context.HttpContext,
                        headers[0]!, context.HttpContext.RequestAborted) is Guid mapped)
                    item.Id = mapped;
                return await next(context);
            });
}
