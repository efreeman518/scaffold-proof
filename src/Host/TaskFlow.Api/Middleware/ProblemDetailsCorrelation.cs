using EF.AspNetCore.ProblemDetails;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace TaskFlow.Api.Middleware;

/// <summary>Applies one correlation contract to every API ProblemDetails response.</summary>
internal static class ProblemDetailsCorrelation
{
    public static void Apply(ProblemDetails problemDetails, HttpContext httpContext)
    {
        ProblemDetailsMetadata.ApplyRequestMetadata(problemDetails, httpContext);

        problemDetails.Extensions.Remove("activityId");
        problemDetails.Extensions["requestId"] = httpContext.TraceIdentifier;

        var activity = Activity.Current;
        if (activity is null)
        {
            problemDetails.Extensions.Remove("traceId");
            problemDetails.Extensions.Remove("spanId");
            return;
        }

        problemDetails.Extensions["traceId"] = activity.TraceId.ToHexString();
        problemDetails.Extensions["spanId"] = activity.SpanId.ToHexString();
    }
}
