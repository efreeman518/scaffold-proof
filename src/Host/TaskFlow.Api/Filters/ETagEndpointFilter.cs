using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Api.Filters;

/// <summary>
/// Emits the strong <c>ETag</c> response header from any result whose value carries an aggregate
/// version. Added once per route group rather than per route, so GETs get an ETag too - without that a
/// client has no way to obtain the token it is required to send back on the next write.
/// </summary>
internal sealed class ETagEndpointFilter : IEndpointFilter
{
    /// <summary>Sets the ETag header when the handler returned a version-carrying payload.</summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        if (result is IValueHttpResult { Value: IETagCarrier carrier } && carrier.ETagVersion is long version)
        {
            context.HttpContext.Response.Headers.ETag =
                $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
        }

        return result;
    }
}
