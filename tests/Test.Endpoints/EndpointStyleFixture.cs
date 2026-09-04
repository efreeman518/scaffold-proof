using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Models;

namespace Test.Endpoints;

/// <summary>
/// Style names used by the dual-style endpoint suite. Every entity contract test runs once per style,
/// which is the only way the "Service and CQRS are the same API" claim stays true as the two evolve.
/// </summary>
public static class EndpointStyles
{
    public const string Service = "Service";
    public const string Cqrs = "Cqrs";

    /// <summary>
    /// TASKFLOW_APPLICATION_STYLE wins over configuration inside ApplicationStyleResolver, so when it
    /// is set the [DataRow] for the other style would silently exercise the forced one and pass for
    /// the wrong reason. Inconclusive is honest; a green tick would not be.
    /// </summary>
    public static void SkipWhenStyleForced()
    {
        var forced = Environment.GetEnvironmentVariable(ApplicationStyleResolver.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(forced))
        {
            Assert.Inconclusive(
                $"{ApplicationStyleResolver.EnvironmentVariable}={forced} overrides configuration; " +
                "the dual-style matrix cannot select a style per test row.");
        }
    }
}

/// <summary>
/// Owns one <see cref="CustomApiFactory"/> per application style for a test class. Each factory has its
/// own in-memory database, so the two styles never see each other's rows.
/// </summary>
public sealed class EndpointStyleFixture : IDisposable
{
    private readonly Dictionary<string, CustomApiFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Returns the factory for a style, creating it on first use.</summary>
    public CustomApiFactory Factory(string style)
    {
        lock (_gate)
        {
            if (!_factories.TryGetValue(style, out var factory))
            {
                factory = new CustomApiFactory(style);
                _factories[style] = factory;
            }
            return factory;
        }
    }

    /// <summary>Creates a client bound to the given application style.</summary>
    public HttpClient CreateClient(string style) => Factory(style).CreateClient();

    /// <summary>Disposes every style factory this fixture created.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var factory in _factories.Values) factory.Dispose();
            _factories.Clear();
        }
    }
}

/// <summary>HTTP helpers for the concurrency contract, so tests read as intent rather than plumbing.</summary>
public static class ConcurrencyHttpExtensions
{
    /// <summary>The strong entity tag value ("12") without quotes, or null when the response carries none.</summary>
    public static string? ETagValue(this HttpResponseMessage response) =>
        response.Headers.ETag?.Tag?.Trim('"');

    /// <summary>Formats an aggregate version as the strong entity tag the API expects.</summary>
    public static string IfMatch(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>PUT with an explicit If-Match header value (pass "*" for the wildcard override).</summary>
    public static Task<HttpResponseMessage> PutWithIfMatchAsync<T>(
        this HttpClient client, string url, T body, string? ifMatch, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        AddIfMatch(request, ifMatch);
        return client.SendAsync(request, ct);
    }

    /// <summary>PATCH with an explicit If-Match header value.</summary>
    public static Task<HttpResponseMessage> PatchWithIfMatchAsync<T>(
        this HttpClient client, string url, T body, string? ifMatch, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        AddIfMatch(request, ifMatch);
        return client.SendAsync(request, ct);
    }

    /// <summary>DELETE with an explicit If-Match header value.</summary>
    public static Task<HttpResponseMessage> DeleteWithIfMatchAsync(
        this HttpClient client, string url, string? ifMatch, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        AddIfMatch(request, ifMatch);
        return client.SendAsync(request, ct);
    }

    // The API serializes enums as strings (ConfigureHttpJsonOptions), so the test client must too.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Reads the entity envelope out of a successful response.</summary>
    public static async Task<T?> ItemAsync<T>(this HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<DefaultResponse<T>>(JsonOptions, ct))!.Item;

    private static void AddIfMatch(HttpRequestMessage request, string? ifMatch)
    {
        if (ifMatch is null) return;

        // TryAddWithoutValidation: the malformed cases (W/"1", "banana") are exactly what the filter
        // must reject, and typed header parsing would refuse to send them.
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
    }
}
