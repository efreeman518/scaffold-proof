using EF.Common.Contracts;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Contracts;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Api.Endpoints;

/// <summary>Maps operator routes for the transactional outbox (D-026). Global admin only, no tenant scope.</summary>
public static class OutboxAdminEndpoints
{
    /// <summary>Registers the outbox admin routes.</summary>
    public static IEndpointRouteBuilder MapOutboxAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/outbox").WithTags("Admin");

        group.MapPost("/{id:guid}/retry", async (
            Guid id,
            [FromServices] IRequestContext<string, Guid?> requestContext,
            [FromServices] IOperationalWorkRepository work,
            CancellationToken ct) =>
        {
            // A dead-lettered row can belong to any tenant, so this is deliberately not tenant-scoped.
            if (!requestContext.Roles.Contains(AppConstants.ROLE_GLOBAL_ADMIN))
                return Results.Forbid();

            var reset = await work.RetryDeadLetteredAsync<OutboxMessage>(id, ct);
            return reset ? Results.NoContent() : Results.NotFound();
        })
        .WithName("RetryOutboxMessage")
        .WithSummary("Resets a dead-lettered outbox message so the dispatcher retries it.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
