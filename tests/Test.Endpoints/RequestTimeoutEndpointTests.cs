using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Test.Endpoints;

/// <summary>
/// D-064: the Api's default request-timeout policy binds from configuration, and the two endpoints that
/// hold a connection open for longer than any fixed budget (the SSE token stream, the NDJSON export) carry
/// the metadata that opts them out of it. Endpoint tier (WebApplicationFactory via <c>CustomApiFactory</c>):
/// the opt-out is endpoint metadata, so it must be read off the real <see cref="EndpointDataSource"/> rather
/// than re-asserted against the mapping code.
/// </summary>
[TestClass]
[TestCategory("Endpoint")]
public sealed class RequestTimeoutEndpointTests
{
    private static CustomApiFactory _factory = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _factory = new CustomApiFactory();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _factory?.Dispose();

    /// <summary>The default policy's timeout is the configured RequestTimeouts:DefaultSeconds (30 here).</summary>
    [TestMethod]
    public void DefaultPolicy_MatchesConfiguredSeconds()
    {
        var options = _factory.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;

        Assert.AreEqual(TimeSpan.FromSeconds(30), options.DefaultPolicy?.Timeout);
    }

    /// <summary>The SSE token stream has no fixed duration, so it must opt out of the default timeout.</summary>
    [TestMethod]
    public void AiChatStreamEndpoint_DisablesTheRequestTimeout()
    {
        var metadata = FindByName("AiChatStream").Metadata.GetMetadata<DisableRequestTimeoutAttribute>();

        Assert.IsNotNull(metadata, "AiChatStream must carry DisableRequestTimeoutAttribute");
    }

    /// <summary>The tenant export holds the connection open for as long as it has rows to send.</summary>
    [TestMethod]
    public void ExportEndpoint_DisablesTheRequestTimeout()
    {
        var metadata = FindByName("ExportTaskItems").Metadata.GetMetadata<DisableRequestTimeoutAttribute>();

        Assert.IsNotNull(metadata, "ExportTaskItems must carry DisableRequestTimeoutAttribute");
    }

    /// <summary>An ordinary request/response endpoint is left on the default policy, not opted out.</summary>
    [TestMethod]
    public void OrdinaryEndpoint_KeepsTheDefaultTimeout()
    {
        var metadata = FindByName("AiStatus").Metadata.GetMetadata<DisableRequestTimeoutAttribute>();

        Assert.IsNull(metadata, "AiStatus is a plain request/response route and must not opt out");
    }

    private static Endpoint FindByName(string endpointName) =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == endpointName);
}
