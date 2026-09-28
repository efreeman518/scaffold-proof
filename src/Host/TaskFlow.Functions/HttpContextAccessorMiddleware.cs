using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace TaskFlow.Functions;

/// <summary>
/// Publishes an HTTP-triggered invocation's <see cref="HttpContext"/> through <see cref="IHttpContextAccessor"/>.
/// Under ASP.NET Core integration the function body runs on the worker's invocation path, not inside the
/// proxied request's execution context, so the accessor is empty there. The request context keys on it: an HTTP
/// trigger with an empty accessor would be treated as no-request (system) work instead of an HTTP request in
/// scaffold auth mode. Queue, blob and timer triggers have no HttpContext and are left untouched.
/// </summary>
internal sealed class HttpContextAccessorMiddleware(IHttpContextAccessor accessor) : IFunctionsWorkerMiddleware
{
    /// <inheritdoc />
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext is null)
        {
            await next(context);
            return;
        }

        accessor.HttpContext = httpContext;
        try
        {
            await next(context);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }
}
