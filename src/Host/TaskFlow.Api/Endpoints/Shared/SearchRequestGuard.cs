using EF.AspNetCore;
using EF.Common.Contracts;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Api.Endpoints.Shared;

/// <summary>
/// First line of every search handler. An out-of-range page size is answered with 400 rather than
/// clamped (GR-18): a silent clamp returns fewer rows than the caller asked for with a 200, and the
/// caller has no way to tell that from "there were no more rows".
/// </summary>
internal static class SearchRequestGuard
{
    /// <summary>Returns a 400 problem result when the page size is outside [Min, Max], otherwise null.</summary>
    public static IResult? Validate(int pageSize) =>
        PageSizeLimits.IsValid(pageSize)
            ? null
            : TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponse(
                statusCodeOverride: StatusCodes.Status400BadRequest,
                message: string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    ErrorConstants.ERROR_PAGE_SIZE_RANGE, PageSizeLimits.Min, PageSizeLimits.Max)));
}
