using Microsoft.Extensions.Primitives;
using System.Globalization;

namespace TaskFlow.Api.Filters;

/// <summary>
/// Bound form of the <c>If-Match</c> request header (GR-16). Handlers take this instead of reading the
/// header themselves, so the precondition reaches the application layer as a value.
///
/// <c>ExpectedVersion</c> is null exactly when the caller sent the wildcard <c>*</c>: the explicit
/// trusted-automation override. The endpoint filter has already rejected a missing (428) or malformed
/// (400) header by the time a handler runs, so "null and not wildcard" never reaches one.
/// </summary>
public readonly record struct IfMatch(long? ExpectedVersion, bool IsWildcard)
{
    /// <summary>Wildcard precondition - matches any current version.</summary>
    public static IfMatch Wildcard => new(null, true);

    /// <summary>
    /// Parses a raw If-Match header value. Accepts the wildcard and a strong entity tag ("12" or a
    /// bare 12); rejects weak tags (<c>W/"12"</c>), which cannot express an exact-version precondition,
    /// and anything else that is not an integer version.
    /// </summary>
    public static bool TryParse(StringValues header, out IfMatch value)
    {
        value = default;

        var raw = header.Count > 0 ? header[0]?.Trim() : null;
        if (string.IsNullOrEmpty(raw)) return false;

        if (raw == "*")
        {
            value = Wildcard;
            return true;
        }

        if (raw.StartsWith("W/", StringComparison.Ordinal)) return false;

        var unquoted = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"'
            ? raw[1..^1]
            : raw;

        if (!long.TryParse(unquoted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)) return false;

        value = new IfMatch(version, false);
        return true;
    }

    /// <summary>
    /// Minimal-API binder. Binding runs before endpoint filters, so an absent or malformed header must
    /// bind to the default rather than throw - <see cref="IfMatchEndpointFilter"/> owns the 428/400.
    /// </summary>
    public static ValueTask<IfMatch> BindAsync(HttpContext context) =>
        ValueTask.FromResult(TryParse(context.Request.Headers.IfMatch, out var value) ? value : default);
}
