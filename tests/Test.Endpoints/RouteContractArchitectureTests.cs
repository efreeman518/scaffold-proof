using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Api.Filters;

namespace Test.Endpoints;

/// <summary>
/// Architectural rules about the route graph itself. They live beside the endpoint suite because the
/// route table only exists once the host is built; a NetArchTest assembly scan cannot see it.
///
/// Both rules exist because their failure mode is silent: a mutating route added without
/// <c>RequireIfMatch()</c> accepts lost updates, and a route added to one style but not the other
/// breaks whichever clients happen to run against the missing one.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class RouteContractArchitectureTests
{
    private static EndpointStyleFixture _fixture = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    /// <summary>Enumerates (method, pattern) pairs for the versioned API routes of one style.</summary>
    private static HashSet<string> RouteSet(string style)
    {
        var endpoints = _fixture.Factory(style).Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>();

        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!pattern.Contains("/api/v", StringComparison.Ordinal)) continue;
            if (pattern.Contains("flowengine", StringComparison.OrdinalIgnoreCase)) continue;

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods) routes.Add($"{method} {pattern}");
        }
        return routes;
    }

    /// <summary>Verifies every mutating non-POST route declares the If-Match precondition.</summary>
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public void Given_MutatingRoutes_When_Mapped_Then_AllNonPostCarryIfMatchMetadata(string style)
    {
        EndpointStyles.SkipWhenStyleForced();

        var offenders = _fixture.Factory(style).Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
            {
                var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
                if (!pattern.Contains("/api/v", StringComparison.Ordinal)) return false;
                if (pattern.Contains("flowengine", StringComparison.OrdinalIgnoreCase)) return false;

                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                var mutatingNonPost = methods.Any(m =>
                    m is "PUT" or "PATCH" or "DELETE");

                return mutatingNonPost && endpoint.Metadata.GetMetadata<IfMatchRequiredMetadata>() is null;
            })
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.IsEmpty(offenders,
            $"PUT/PATCH/DELETE routes without RequireIfMatch() accept lost updates: {string.Join(", ", offenders)}");
    }

    /// <summary>Verifies the two application styles expose exactly the same public route set.</summary>
    [TestMethod]
    public void Given_BothStyles_When_Mapped_Then_RouteSetsAreIdentical()
    {
        EndpointStyles.SkipWhenStyleForced();

        var service = RouteSet(EndpointStyles.Service);
        var cqrs = RouteSet(EndpointStyles.Cqrs);

        var onlyService = service.Except(cqrs).OrderBy(r => r, StringComparer.Ordinal).ToList();
        var onlyCqrs = cqrs.Except(service).OrderBy(r => r, StringComparer.Ordinal).ToList();

        Assert.IsEmpty(onlyService, $"Routes missing from the CQRS style: {string.Join(", ", onlyService)}");
        Assert.IsEmpty(onlyCqrs, $"Routes missing from the Service style: {string.Join(", ", onlyCqrs)}");
        Assert.IsGreaterThan(0, service.Count, "The route scan found nothing, so it proves nothing.");
    }
}
