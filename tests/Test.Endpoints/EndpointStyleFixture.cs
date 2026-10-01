using System.Net.Http.Json;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Models;
using Test.Support;

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

/// <summary>
/// Reads the API response envelope. The If-Match / ETag request helpers come from
/// <see cref="EF.Testing.Http.ConcurrencyHttpExtensions"/>, given <see cref="JsonTestOptions.Default"/> so request
/// bodies serialize enums as strings like the API does.
/// </summary>
public static class ResponseEnvelopeExtensions
{
    /// <summary>Reads the entity envelope out of a successful response.</summary>
    public static async Task<T?> ItemAsync<T>(this HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<DefaultResponse<T>>(JsonTestOptions.Default, ct))!.Item;
}
