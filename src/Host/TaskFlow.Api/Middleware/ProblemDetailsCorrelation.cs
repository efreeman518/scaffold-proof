using EF.AspNetCore.ProblemDetails;
using Microsoft.AspNetCore.Mvc;

namespace TaskFlow.Api.Middleware;

/// <summary>Applies one correlation contract to every API ProblemDetails response.</summary>
internal static class ProblemDetailsCorrelation
{
    public static void Apply(ProblemDetails problemDetails, HttpContext httpContext) =>
        ProblemDetailsMetadata.Apply(problemDetails, httpContext);
}
