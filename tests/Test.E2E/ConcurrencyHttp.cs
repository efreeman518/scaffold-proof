using System.Globalization;
using System.Net.Http.Json;

namespace Test.E2E;

/// <summary>
/// If-Match / ETag helpers for the E2E lane. Kept local to this project so the SQL-backed tests read as
/// intent rather than request plumbing.
/// </summary>
internal static class ConcurrencyHttp
{
    /// <summary>The strong entity tag value without quotes, or null when the response carries none.</summary>
    public static string? ETagValue(this HttpResponseMessage response) =>
        response.Headers.ETag?.Tag?.Trim('"');

    /// <summary>Formats an aggregate version as the strong entity tag the API expects.</summary>
    public static string IfMatch(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Formats a raw ETag header value (as returned by <see cref="ETagValue"/>) for If-Match.</summary>
    public static string IfMatch(string? etagValue) => $"\"{etagValue}\"";

    /// <summary>PUT carrying an If-Match precondition.</summary>
    public static Task<HttpResponseMessage> PutWithIfMatchAsync<T>(
        this HttpClient client, string url, T body, string ifMatch, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request, ct);
    }

    /// <summary>DELETE carrying an If-Match precondition.</summary>
    public static Task<HttpResponseMessage> DeleteWithIfMatchAsync(
        this HttpClient client, string url, string ifMatch, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request, ct);
    }
}
