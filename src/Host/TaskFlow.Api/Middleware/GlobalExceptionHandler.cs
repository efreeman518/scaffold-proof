using EF.Common.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TaskFlow.Api.Middleware;

/// <summary>
/// Converts unhandled exceptions into ProblemDetails responses and applies log severity by
/// failure class so client errors and disconnects do not pollute server-error dashboards.
/// </summary>
internal sealed class DefaultExceptionHandler(
    ILogger<DefaultExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    /// <summary>
    /// Maps the exception to a status and writes the ProblemDetails body.
    /// <para>
    /// A 412 carries no ETag from here: <c>ExceptionHandlerMiddleware</c> registers an OnStarting callback that
    /// clears ETag and cache headers before any handler runs, so a header set here never reaches the client.
    /// The current version on a 412 comes only from <c>IfMatchEndpointFilter</c>, which answers before the
    /// handler runs.
    /// </para>
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Only a cancellation the caller caused is a 499. A downstream HttpClient timeout or an internal
        // token also surfaces as OperationCanceledException, and relabelling those as client disconnects
        // would hide server-side timeouts from error telemetry.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.RequestCancelledByClient(httpContext.Request.Path);
            if (!httpContext.Response.HasStarted)
                httpContext.Response.StatusCode = StatusCodeClientClosedRequest;
            return true;
        }

        var (statusCode, title) = exception switch
        {
            // D-032: a stale If-Match and a lost update between load and save are the same failure to
            // the caller - 412, not the 409 this used to answer (which no client could act on).
            // ConcurrencyGuard.Require and a throwing-policy save raise PreconditionFailedException; a
            // policy-free save still raises the raw DbUpdateConcurrencyException.
            PreconditionFailedException or DbUpdateConcurrencyException
                => (StatusCodes.Status412PreconditionFailed, "Precondition failed"),
            ConflictException
                => (StatusCodes.Status409Conflict, "Conflict"),
            UnauthorizedAccessException
                => (StatusCodes.Status403Forbidden, "Forbidden"),
            OperationCanceledException when HasTimeoutInChain(exception)
                => (StatusCodes.Status504GatewayTimeout, "Gateway timeout"),
            BadHttpRequestException
                => (StatusCodes.Status400BadRequest, "Bad request"),
            // TaskFlow throws ArgumentException on purpose for caller input it rejects (page size, cursor and
            // continuation tokens). The same type thrown by framework or library code - and every
            // KeyNotFoundException, FormatException and InvalidOperationException - is a server fault, so it
            // stays a 500 instead of being relabelled as the caller's mistake.
            ArgumentException when ThrownByApp(exception)
                => (StatusCodes.Status400BadRequest, "Bad request"),
            _
                => (StatusCodes.Status500InternalServerError, "Internal server error")
        };

        // Client errors are not server errors; log them at lower severity to avoid flooding error dashboards.
        if (statusCode < 500)
            logger.ClientError(exception, statusCode, exception.GetType().Name, exception.Message);
        else
            logger.UnhandledException(exception, exception.GetType().Name, exception.Message);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            // Exception text never leaves the process for a server fault outside Development: SQL,
            // connection and internal messages are for the log, not an anonymous caller.
            Detail = environment.IsDevelopment() ? exception.ToString()
                : statusCode < 500 ? exception.Message
                : null,
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}"
        };

        ProblemDetailsCorrelation.Apply(problemDetails, httpContext);

        if (httpContext.Response.HasStarted)
            return true;

        httpContext.Response.StatusCode = statusCode;

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

    /// <summary>Non-standard status for a request the client abandoned (nginx convention).</summary>
    private const int StatusCodeClientClosedRequest = 499;

    /// <summary>True when an HttpClient or other timeout is the cause of the cancellation.</summary>
    private static bool HasTimeoutInChain(Exception exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is TimeoutException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when TaskFlow code threw the exception itself (the throwing method lives in a TaskFlow assembly),
    /// as opposed to a guard or fault inside the BCL, ASP.NET Core, EF Core or a package.
    /// </summary>
    private static bool ThrownByApp(Exception exception) =>
        exception.TargetSite?.DeclaringType?.Assembly.GetName().Name?
            .StartsWith(AppAssemblyPrefix, StringComparison.Ordinal) == true;

    private const string AppAssemblyPrefix = "TaskFlow.";
}
