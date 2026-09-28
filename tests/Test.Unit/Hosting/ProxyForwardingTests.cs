using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace Test.Unit.Hosting;

/// <summary>Verifies reverse-proxy metadata is accepted only from the configured trust boundary.</summary>
[TestClass]
public sealed class ProxyForwardingTests
{
    [TestMethod]
    public async Task TrustedProxy_AppliesPublicSchemeHostAndConfiguredPathBase()
    {
        await using var app = await CreateAppAsync(
            IPAddress.Parse("10.0.0.10"),
            new Dictionary<string, string?>
            {
                ["Proxy:ForwardedHeaders:Enabled"] = "true",
                ["Proxy:ForwardedHeaders:ForwardLimit"] = "1",
                ["Proxy:ForwardedHeaders:KnownProxies:0"] = "10.0.0.10",
                ["Proxy:ForwardedHeaders:AllowedHosts:0"] = "public.example",
                ["Proxy:PathBase"] = "/admin"
            },
            TestContext.CancellationToken);

        using var client = app.GetTestClient();
        using var request = CreateForwardedRequest("http://internal:8080/admin/probe");
        using var response = await client.SendAsync(request, TestContext.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<RequestMetadata>(TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.AreEqual("https", result.Scheme);
        Assert.AreEqual("public.example", result.Host);
        Assert.AreEqual("/admin", result.PathBase);
        Assert.AreEqual("/probe", result.Path);
    }

    [TestMethod]
    public async Task UntrustedClient_CannotForgeForwardedSchemeOrHost()
    {
        await using var app = await CreateAppAsync(
            IPAddress.Parse("203.0.113.20"),
            new Dictionary<string, string?>
            {
                ["Proxy:ForwardedHeaders:Enabled"] = "true",
                ["Proxy:ForwardedHeaders:ForwardLimit"] = "1",
                ["Proxy:ForwardedHeaders:KnownProxies:0"] = "10.0.0.10"
            },
            TestContext.CancellationToken);

        using var client = app.GetTestClient();
        using var request = CreateForwardedRequest("http://internal:8080/probe");
        using var response = await client.SendAsync(request, TestContext.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<RequestMetadata>(TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.AreEqual("http", result.Scheme);
        Assert.AreEqual("internal:8080", result.Host);
    }

    [TestMethod]
    public void IsolatedNetworkOptIn_ClearsTrustListsAndHopLimit()
    {
        var builder = TestWebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxy:ForwardedHeaders:Enabled"] = "true",
            ["Proxy:ForwardedHeaders:TrustAllProxies"] = "true"
        });
        builder.AddProxyForwarding();

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.IsNull(options.ForwardLimit);
        Assert.IsEmpty(options.KnownProxies);
        Assert.IsEmpty(options.KnownIPNetworks);
    }

    /// <summary>
    /// Behind Container Apps ingress (no fixed proxy subnet to allowlist) the gateway trusts the connecting hop
    /// and reads exactly one X-Forwarded-For entry: the one ingress appended. A client-supplied entry to its
    /// left must not become the remote address, or a caller could pick its own edge rate-limit partition.
    /// </summary>
    [TestMethod]
    public async Task TrustAllProxiesWithForwardLimit_UsesOnlyTheIngressAppendedClientAddress()
    {
        await using var app = await CreateAppAsync(
            IPAddress.Parse("100.100.0.7"),
            new Dictionary<string, string?>
            {
                ["Proxy:ForwardedHeaders:Enabled"] = "true",
                ["Proxy:ForwardedHeaders:TrustAllProxies"] = "true",
                ["Proxy:ForwardedHeaders:ForwardLimit"] = "1"
            },
            TestContext.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://internal:8080/probe");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6, 198.51.100.42");
        using var response = await client.SendAsync(request, TestContext.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<RequestMetadata>(TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.AreEqual("198.51.100.42", result.RemoteIp);
    }

    private static async Task<WebApplication> CreateAppAsync(
        IPAddress remoteAddress,
        IReadOnlyDictionary<string, string?> configuration,
        CancellationToken cancellationToken)
    {
        var builder = TestWebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddProxyForwarding();

        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = remoteAddress;
            return next(context);
        });
        app.UseProxyForwarding();
        app.Run(context => context.Response.WriteAsJsonAsync(new RequestMetadata(
            context.Request.Scheme,
            context.Request.Host.Value ?? "",
            context.Request.PathBase.Value ?? "",
            context.Request.Path.Value ?? "",
            context.Connection.RemoteIpAddress?.ToString() ?? "")));
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static HttpRequestMessage CreateForwardedRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Forwarded-For", "198.51.100.42");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "public.example");
        return request;
    }

    private sealed record RequestMetadata(string Scheme, string Host, string PathBase, string Path, string RemoteIp);

    public TestContext TestContext { get; set; } = null!;
}
