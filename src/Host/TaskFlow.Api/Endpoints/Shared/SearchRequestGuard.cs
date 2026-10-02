using EF.AspNetCore;
using EF.Common.Contracts;
using TaskFlow.Application.Contracts;
using TaskFlow.Domain.Shared.Constants;

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
            : TypedResults.Problem(ProblemDetailsHelper.Create(
                StatusCodes.Status400BadRequest,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    ErrorConstants.ERROR_PAGE_SIZE_RANGE, PageSizeLimits.Min, PageSizeLimits.Max)));

    /// <summary>Most media types one attachment search may filter on.</summary>
    internal const int MaxContentTypeFilters = 10;

    /// <summary>
    /// Returns a 400 problem result when an attachment search content type filter is empty, holds more than
    /// <see cref="MaxContentTypeFilters"/> entries, or an entry that is not a concrete "type/subtype" within the content
    /// type column length (<see cref="DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX"/>), otherwise null.
    /// </summary>
    public static IResult? ValidateContentTypes(IReadOnlyCollection<string>? contentTypes) =>
        contentTypes is null
        || (contentTypes.Count is > 0 and <= MaxContentTypeFilters
            && contentTypes.All(c => c is not null && c.Length <= DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX
                && TaskFlow.Application.Contracts.Storage.AttachmentMediaType.IsValid(c)))
            ? null
            : TypedResults.Problem(ProblemDetailsHelper.Create(
                StatusCodes.Status400BadRequest,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    ErrorConstants.ERROR_CONTENT_TYPE_FILTER_INVALID, MaxContentTypeFilters, DomainConstants.RULE_ATTACHMENT_CONTENTTYPE_LENGTH_MAX)));

    /// <summary>
    /// Returns a 400 problem result when a task search tag name filter is blank or longer than a tag name can be
    /// (<see cref="DomainConstants.RULE_TAG_NAME_LENGTH_MAX"/>), otherwise null. Such a filter could match no tag.
    /// </summary>
    public static IResult? ValidateTagName(string? tagName) =>
        tagName is null || (!string.IsNullOrWhiteSpace(tagName) && tagName.Trim().Length <= DomainConstants.RULE_TAG_NAME_LENGTH_MAX)
            ? null
            : TypedResults.Problem(ProblemDetailsHelper.Create(
                StatusCodes.Status400BadRequest,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    ErrorConstants.ERROR_TAG_NAME_FILTER_INVALID, DomainConstants.RULE_TAG_NAME_LENGTH_MAX)));
}
