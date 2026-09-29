using EF.IntegrationTesting.AspNetCore;
using TaskFlow.Api.Filters;

namespace Test.Endpoints;

/// <summary>
/// Architectural rules about the route graph itself. They live beside the endpoint suite because the
/// route table only exists once the host is built; an assembly scan cannot see it.
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

    /// <summary>
    /// The versioned API routes of one style, one entry per (method, pattern). A route mapped without an HTTP
    /// method is listed with method <c>*</c> (it accepts every verb), never dropped.
    /// </summary>
    private static IEnumerable<RouteEntry> ApiRoutes(string style) =>
        RouteInventory.From(_fixture.Factory(style).Services)
            .Where(route => route.Pattern.Contains("/api/v", StringComparison.Ordinal)
                && !route.Pattern.Contains("flowengine", StringComparison.OrdinalIgnoreCase));

    /// <summary>Enumerates (method, pattern) pairs for the versioned API routes of one style.</summary>
    private static HashSet<string> RouteSet(string style) =>
        ApiRoutes(style).Select(route => $"{route.Method} {route.Pattern}").ToHashSet(StringComparer.Ordinal);

    /// <summary>Verifies every mutating non-POST route declares the If-Match precondition.</summary>
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public void Given_MutatingRoutes_When_Mapped_Then_AllNonPostCarryIfMatchMetadata(string style)
    {
        EndpointStyles.SkipWhenStyleForced();

        // A method-less route ("*") accepts PUT/PATCH/DELETE too, so it needs the precondition as well.
        var offenders = ApiRoutes(style)
            .Where(route => route.Method is "PUT" or "PATCH" or "DELETE" or "*"
                && route.Metadata.GetMetadata<IfMatchRequiredMetadata>() is null)
            .Select(route => $"{route.Method} {route.Pattern}")
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
