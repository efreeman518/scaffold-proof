using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Globalization;
using TaskFlow.Application.Contracts.Concurrency;

namespace TaskFlow.Api.Middleware;

/// <summary>
/// Converts unhandled exceptions into ProblemDetails responses and applies log severity by
/// failure class so client errors and disconnects do not pollute server-error dashboards.
/// </summary>
internal sealed class DefaultExceptionHandler(
    ILogger<DefaultExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    /// <summary>Provides the try handle operation for default exception handler.</summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, title) = exception switch
        {
            // D-032: a stale If-Match and a lost update between load and save are the same failure to
            // the caller - 412, not the 409 this used to answer (which no client could act on).
            // These arms must stay above the ArgumentException/InvalidOperationException arm below,
            // which would otherwise swallow them into a 400.
            ConcurrencyMismatchException
                => (StatusCodes.Status412PreconditionFailed, "Precondition failed"),
            DbUpdateConcurrencyException
                => (StatusCodes.Status412PreconditionFailed, "Precondition failed"),
            IdempotentCreateConflictException
                => (StatusCodes.Status409Conflict, "Conflict"),
            UnauthorizedAccessException
                => (StatusCodes.Status403Forbidden, "Forbidden"),
            KeyNotFoundException
                => (StatusCodes.Status404NotFound, "Not found"),
            OperationCanceledException
                => (499, "Client closed request"),
            BadHttpRequestException
                => (StatusCodes.Status400BadRequest, "Bad request"),
            ArgumentException or FormatException or InvalidOperationException
                => (StatusCodes.Status400BadRequest, "Bad request"),
            _
                => (StatusCodes.Status500InternalServerError, "Internal server error")
        };

        // Client disconnections and bad-request exceptions are not server errors;
        // log them at lower severity to avoid flooding error dashboards.
        if (exception is OperationCanceledException)
            logger.RequestCancelledByClient(httpContext.Request.Path);
        else if (statusCode < 500)
            logger.ClientError(exception, statusCode, exception.GetType().Name, exception.Message);
        else
            logger.UnhandledException(exception, exception.GetType().Name, exception.Message);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = environment.IsDevelopment() || environment.IsStaging()
                ? exception.ToString()
                : exception.Message,
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}"
        };

        problemDetails.Extensions.TryAdd("traceId", httpContext.TraceIdentifier);
        var activity = Activity.Current;
        if (!string.IsNullOrWhiteSpace(activity?.Id))
            problemDetails.Extensions.TryAdd("activityId", activity.Id);

        if (httpContext.Response.HasStarted)
            return true;

        httpContext.Response.StatusCode = statusCode;

        // Hand the caller the version it needs to retry with, so a 412 is self-correcting instead of
        // forcing an extra GET.
        if (exception is ConcurrencyMismatchException mismatch)
        {
            httpContext.Response.Headers.ETag =
                $"\"{mismatch.Current.ToString(CultureInfo.InvariantCulture)}\"";
        }

        try
        {
            await httpContext.Response.WriteAsJsonAsync(problemDetails, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected while writing the error response.
        }

        return true;
    }
}
