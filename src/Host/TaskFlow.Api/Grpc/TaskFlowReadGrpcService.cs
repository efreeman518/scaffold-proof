using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Models;
using TaskFlow.Contracts.Grpc;

namespace TaskFlow.Api.Grpc;

/// <summary>
/// D-054: the internal gRPC read surface. It is a transport adapter and nothing more - every answer
/// comes from the same application services the REST routes call, so the two styles cannot drift.
///
/// Authorization and tenant context are NOT re-implemented here. A gRPC call is an ordinary HTTP/2
/// request through this host's pipeline: UseAuthentication runs the Scaffold scheme, MapGrpcService is
/// covered by RequireAuthorization and by the authenticated-user fallback policy, and the scoped
/// IRequestContext is built from IHttpContextAccessor.HttpContext.User exactly as it is for a REST
/// call. The tenant a caller sees over gRPC is therefore the tenant it would see over REST.
///
/// Failures are translated by <c>EF.Grpc.ServiceErrorInterceptor</c>, registered in RegisterApiServices
/// with <see cref="StatusFor"/> as its <c>StatusCodeMapper</c>. The mapping mirrors
/// DefaultExceptionHandler's HTTP status choices one for one, so a client that understands the REST
/// failure modes understands these:
/// <list type="table">
/// <item><term>ConcurrencyMismatchException / DbUpdateConcurrencyException</term><description>412 -> FailedPrecondition</description></item>
/// <item><term>IdempotentCreateConflictException</term><description>409 -> Aborted</description></item>
/// <item><term>UnauthorizedAccessException</term><description>403 -> PermissionDenied</description></item>
/// <item><term>KeyNotFoundException</term><description>404 -> NotFound</description></item>
/// <item><term>OperationCanceledException</term><description>499 -> Cancelled</description></item>
/// <item><term>ArgumentException / FormatException / InvalidOperationException</term><description>400 -> InvalidArgument</description></item>
/// <item><term>anything else</term><description>500 -> Internal</description></item>
/// </list>
/// A missing task is not an exception on either transport: REST answers 404 from the Result's None
/// arm, and this answers NotFound from the same arm.
/// </summary>
internal sealed class TaskFlowReadGrpcService(
    ITaskFlowReadService reads,
    // The task-by-id read is the one style-specific dependency. Application:Style decides which of these
    // two is in the container (RegisterServices.Application), so both are optional constructor
    // parameters and exactly one is non-null at runtime - the same branch SetupApiEndpoints already
    // makes for the REST routes, without a service-locator lookup inside the call.
    ITaskItemService? taskItemService = null,
    IRequestHandler<GetTaskItemByIdQuery, Result<DefaultResponse<TaskItemDto>>>? taskItemByIdHandler = null)
    : TaskFlowRead.TaskFlowReadBase
{
    /// <inheritdoc />
    public override async Task<TaskItemSummary> GetTaskItemSummary(
        GetTaskItemSummaryRequest request, ServerCallContext context) =>
        (await reads.GetTaskItemSummaryAsync(context.CancellationToken)).ToProto();

    /// <inheritdoc />
    public override async Task<TaskMetadata> GetTaskMetadata(
        GetTaskMetadataRequest request, ServerCallContext context) =>
        (await reads.GetTaskMetadataAsync(context.CancellationToken)).ToProto();

    /// <inheritdoc />
    public override async Task<Contracts.Grpc.TaskItem> GetTaskItem(
        GetTaskItemRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.Id}' is not a task id."));

        var result = await GetTaskItemResultAsync(id, context.CancellationToken);

        return result.Match(
            response => response.Item is null
                ? throw new RpcException(new Status(StatusCode.NotFound, $"Task item {id} was not found."))
                : response.Item.ToProto(),
            errors => throw new RpcException(new Status(
                StatusCode.InvalidArgument, string.Join("; ", errors.Select(e => e.Message)))),
            () => throw new RpcException(new Status(StatusCode.NotFound, $"Task item {id} was not found.")));
    }

    /// <summary>
    /// The gRPC equivalent of DefaultExceptionHandler's HTTP status selection, handed to
    /// <c>EF.Grpc.ErrorInterceptorSettings.StatusCodeMapper</c>. An <see cref="RpcException"/> a handler
    /// raised deliberately keeps the status it chose; the interceptor re-wraps it with a generic detail so
    /// no exception text reaches the wire.
    /// </summary>
    internal static StatusCode StatusFor(Exception exception) => exception switch
    {
        RpcException rpcException => rpcException.StatusCode,
        ConcurrencyMismatchException => StatusCode.FailedPrecondition,
        DbUpdateConcurrencyException => StatusCode.FailedPrecondition,
        IdempotentCreateConflictException => StatusCode.Aborted,
        UnauthorizedAccessException => StatusCode.PermissionDenied,
        KeyNotFoundException => StatusCode.NotFound,
        OperationCanceledException => StatusCode.Cancelled,
        ArgumentException or FormatException or InvalidOperationException => StatusCode.InvalidArgument,
        _ => StatusCode.Internal
    };

    /// <summary>Routes the by-id read to whichever application style this host was configured with.</summary>
    private Task<Result<DefaultResponse<TaskItemDto>>> GetTaskItemResultAsync(Guid id, CancellationToken ct)
    {
        if (taskItemService is not null) return taskItemService.GetAsync(id, ct);
        if (taskItemByIdHandler is not null) return taskItemByIdHandler.HandleAsync(new GetTaskItemByIdQuery(id), ct);

        // Unreachable through the normal registration chain; loud rather than a NullReferenceException if
        // a future style is added without a task read.
        throw new InvalidOperationException(
            "Neither ITaskItemService nor the GetTaskItemByIdQuery handler is registered; " +
            "Application:Style must select one of them.");
    }
}
