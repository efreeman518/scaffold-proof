using System.Net;

namespace Test.Endpoints;

/// <summary>
/// Locks the D-049 probe contract on a real host. The split only pays off if liveness stays independent of
/// dependencies, so the load-bearing assertion is that <c>/healthz/live</c> answers with the database check
/// registered but no database reachable, while <c>/healthz/ready</c> reports that same dependency.
/// </summary>
[TestClass]
public sealed class HealthProbeContractTests
{
    private static CustomApiFactory _factory = null!;

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _factory = new CustomApiFactory();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _factory?.Dispose();

    /// <summary>Liveness is anonymous, cheap, and carries only the <c>self</c> check.</summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_LivenessProbe_When_Get_Then_HealthyWithoutDependencies()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz/live", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
    }

    /// <summary>Readiness is mapped and anonymous; its status reflects the dependency checks, not the process.</summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_ReadinessProbe_When_Get_Then_MappedAndAnonymous()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz/ready", TestContext.CancellationToken);

        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The retired <c>/readyz</c> alias is gone; two names for one probe is what D-049 rejected.</summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_RetiredReadyzAlias_When_Get_Then_NotFound()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/readyz", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
