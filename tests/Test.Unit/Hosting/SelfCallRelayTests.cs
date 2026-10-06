using System.Net;
using System.Security.Claims;
using Azure.Core;
using EF.Auth.Relay;
using EF.FlowEngine;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Definition;
using EF.FlowEngine.Model;
using EF.Testing.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts;
using TaskFlow.Bootstrapper;

namespace Test.Unit.Hosting;

/// <summary>
/// The workflow self-call relay (D-068) on the real <c>taskflow-api</c> client registration: with
/// <c>FlowEngine:SelfCall:TokenScope</c> set, a call made by a node of an instance carries an app-only token and a relay
/// header with that instance's tenant, the workflow subject and the one tenant role, under the shipped Scheduler
/// <c>ForwardedClaims</c> section, and the Api's shipped section decodes it; a call outside an instance with a tenant is
/// never sent; without the scope nothing is added. A relay header a node supplies is never forwarded. Pure-unit tier:
/// a stub transport and a fixed credential are the only collaborators.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SelfCallRelayTests
{
    private const string Scope = "api://taskflow-api/.default";
    private const string RelayHeader = "X-Forwarded-User-Claims";
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RelayConfigured_CallInAnInstance_CarriesTheTokenAndTheInstanceTenant()
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);

        await SendInInstanceAsync(provider, TenantB.ToString());

        var request = transport.Requests.Single();
        CollectionAssert.AreEqual(new[] { "Bearer token-for:" + Scope }, Header(request, "Authorization").ToArray(),
            "an app-only token for the configured scope");
        var claims = DecodeAsTheApi(request);
        CollectionAssert.AreEquivalent(
            new[]
            {
                ("tenant_id", TenantB.ToString()),
                ("sub", SelfCallRelayHandler.Subject),
                (ClaimTypes.Name, SelfCallRelayHandler.DisplayName),
                (ClaimTypes.Role, AppConstants.ROLE_TENANT_MEMBER),
            },
            claims.Select(c => (c.Type, c.Value)).ToArray(),
            "exactly the instance tenant, the workflow subject and name, and the tenant member role");
    }

    [TestMethod]
    public void RelayedRoles_AreOneTenantRole_NeverACrossTenantRole()
    {
        CollectionAssert.AreEqual(new[] { AppConstants.ROLE_TENANT_MEMBER }, SelfCallRelayHandler.Roles.ToArray());
        CollectionAssert.DoesNotContain(SelfCallRelayHandler.Roles.ToArray(), AppConstants.ROLE_GLOBAL_ADMIN);
        CollectionAssert.DoesNotContain(SelfCallRelayHandler.Roles.ToArray(), AppConstants.ROLE_SYSTEM);
    }

    [TestMethod]
    public async Task RelayConfigured_NodeSuppliedRelayHeader_IsReplaced()
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);
        var forged = ForwardedClaimsCodec.Encode(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, AppConstants.ROLE_GLOBAL_ADMIN)], "forged")),
            new ForwardedClaimsOptions { ClaimTypes = [ClaimTypes.Role] });

        await SendInInstanceAsync(provider, TenantB.ToString(), request => request.Headers.Add(RelayHeader, forged));

        var request = transport.Requests.Single();
        Assert.HasCount(1, Header(request, RelayHeader));
        Assert.IsFalse(DecodeAsTheApi(request).Any(c => c.Value == AppConstants.ROLE_GLOBAL_ADMIN));
        Assert.AreEqual(TenantB.ToString(), DecodeAsTheApi(request).Single(c => c.Type == "tenant_id").Value);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not-a-tenant")]
    public async Task RelayConfigured_NoInstanceTenant_FailsWithoutSending(string? instanceTenant)
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SendInInstanceAsync(provider, instanceTenant));

        StringAssert.Contains(thrown.Message, "FlowEngine:SelfCall:TokenScope");
        Assert.IsEmpty(transport.Requests, "the call never falls back to the host's own identity");
    }

    [TestMethod]
    public async Task RelayConfigured_OutsideAnyInstance_FailsWithoutSending()
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(RegisterServices.TaskFlowApiClientName);

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.GetAsync(new Uri("http://localhost/api/v1/task-items/x"), TestContext.CancellationToken));

        Assert.IsEmpty(transport.Requests);
    }

    [TestMethod]
    public async Task RelayNotConfigured_SendsNoTokenAndNoRelayHeader()
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, scope: null);

        await SendInInstanceAsync(provider, TenantB.ToString(), request => request.Headers.Add(RelayHeader, "node-supplied"));

        var request = transport.Requests.Single();
        Assert.IsEmpty(Header(request, "Authorization"));
        Assert.IsEmpty(Header(request, RelayHeader));
    }

    [TestMethod]
    public void RelayConfigured_AllowlistDroppingARelayedClaim_FailsHostStart()
    {
        // The package default allowlist (no ClaimTypes set) has no tenant claim type.
        using var provider = Build(StubHttpMessageHandler.Returns(HttpStatusCode.OK), Scope, shippedSettings: false);

        var thrown = Assert.ThrowsExactly<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SelfCallRelayOptions>>().Value);
        StringAssert.Contains(thrown.Message, "ForwardedClaims:ClaimTypes");
    }

    [TestMethod]
    public void Registration_WrapsTheHttpNodeExecutors_AndNoOther()
    {
        using var provider = Build(StubHttpMessageHandler.Returns(HttpStatusCode.OK), scope: null);
        var registry = provider.GetRequiredService<INodeExecutorRegistry>();

        Assert.IsInstanceOfType<InstanceTenantNodeExecutor>(registry.Get("integration"));
        Assert.IsInstanceOfType<InstanceTenantNodeExecutor>(registry.Get("fetch"));
        Assert.IsNotInstanceOfType<InstanceTenantNodeExecutor>(registry.Get("agent"));
        Assert.AreEqual("integration", registry.Get("integration")!.NodeType);
    }

    [TestMethod]
    public async Task InstanceTenant_IsScopedToTheNode()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string?>();
        var executor = new InstanceTenantNodeExecutor(new CallbackExecutor(() => seen.Add(InstanceTenantNodeExecutor.CurrentTenant)));

        await executor.ExecuteAsync(new NodeDefinition { Id = "n", Type = "integration" }, Instance("t-1"), null!, TestContext.CancellationToken);
        await Task.WhenAll(
            executor.ExecuteAsync(new NodeDefinition { Id = "n", Type = "integration" }, Instance("t-2"), null!, TestContext.CancellationToken),
            executor.ExecuteAsync(new NodeDefinition { Id = "n", Type = "integration" }, Instance("t-3"), null!, TestContext.CancellationToken));

        CollectionAssert.AreEquivalent(new[] { "t-1", "t-2", "t-3" }, seen);
        Assert.IsNull(InstanceTenantNodeExecutor.CurrentTenant, "nothing leaks out of the node");
    }

    [TestMethod]
    public void CanActFor_RelayConfigured_AnyTenant_OtherwiseTheScaffoldTenantOnly()
    {
        var scaffold = Guid.Parse(ScaffoldPrincipal.TenantId);
        var off = new SelfCallRelayOptions { TokenScope = "  " };
        var on = new SelfCallRelayOptions { TokenScope = Scope };

        Assert.AreEqual(scaffold, SelfCallRelayOptions.ScaffoldTenantId);
        Assert.IsTrue(off.CanActFor(scaffold));
        Assert.IsFalse(off.CanActFor(TenantB));
        Assert.IsTrue(on.CanActFor(scaffold));
        Assert.IsTrue(on.CanActFor(TenantB));
    }

    /// <summary>Sends one self-call from inside a node of an instance with <paramref name="tenant"/>.</summary>
    private Task SendInInstanceAsync(ServiceProvider provider, string? tenant, Action<HttpRequestMessage>? configure = null)
    {
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var executor = new InstanceTenantNodeExecutor(new CallbackExecutor(async () =>
        {
            using var client = factory.CreateClient(RegisterServices.TaskFlowApiClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("http://localhost/api/v1/task-items/search"));
            configure?.Invoke(request);
            using var response = await client.SendAsync(request, TestContext.CancellationToken);
        }));
        return executor.ExecuteAsync(new NodeDefinition { Id = "n", Type = "integration" }, Instance(tenant), null!, TestContext.CancellationToken);
    }

    /// <summary>The relay header decoded with the Api's shipped <c>ForwardedClaims</c> section, as its relay does.</summary>
    private static IReadOnlyList<Claim> DecodeAsTheApi(RecordedHttpRequest request)
    {
        var api = new ForwardedClaimsOptions();
        new ConfigurationBuilder().AddJsonFile(RepoRoot.Combine("src", "Host", "TaskFlow.Api", "appsettings.json"), optional: false).Build()
            .GetSection(ForwardedClaimsOptions.ConfigSectionName).Bind(api);
        Assert.IsTrue(ForwardedClaimsCodec.TryDecode(Header(request, api.HeaderName).Single(), api, out var claims));
        return claims;
    }

    private static IReadOnlyList<string> Header(RecordedHttpRequest request, string name) =>
        request.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value ?? [];

    /// <summary>
    /// The self-call client as the Bootstrapper registers it, over the Scheduler's shipped settings plus the scope, with
    /// a fixed credential registered ahead (<c>AddAzureTokenCredential</c> is a try-add) and the stub as transport.
    /// </summary>
    private static ServiceProvider Build(
        StubHttpMessageHandler transport,
        string? scope,
        bool shippedSettings = true)
    {
        var builder = new ConfigurationBuilder();
        if (shippedSettings)
            builder.AddJsonFile(RepoRoot.Combine("src", "Host", "TaskFlow.Scheduler", "appsettings.json"), optional: false);
        var config = builder
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FlowEngine:SelfCall:TokenScope"] = scope })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TokenCredential>(new FixedCredential());
        var fe = services.AddFlowEngine();
        RegisterServices.RunHttpNodesInTheInstanceTenant(services);
        RegisterServices.AddTaskFlowApiHttpClient(fe, services, config).ConfigurePrimaryHttpMessageHandler(() => transport);
        return services.BuildServiceProvider();
    }

    private static ExecutionInstance Instance(string? tenant) => new()
    {
        InstanceId = Guid.CreateVersion7().ToString("N"),
        WorkflowId = "compliance-check",
        TenantId = tenant,
        Status = ExecStatus.Running,
    };

    /// <summary>Runs a callback as the node's work.</summary>
    private sealed class CallbackExecutor(Func<Task> work) : INodeExecutor
    {
        public CallbackExecutor(Action work) : this(() => { work(); return Task.CompletedTask; })
        {
        }

        public string NodeType => "integration";

        public async Task<NodeResult> ExecuteAsync(NodeDefinition node, ExecutionInstance instance, IExecutionContext ctx, CancellationToken ct = default)
        {
            await Task.Yield();
            await work();
            return new NodeResult { Outcome = DecisionOutcome.Match };
        }
    }

    /// <summary>Issues a token naming the scope it was asked for.</summary>
    private sealed class FixedCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new($"token-for:{string.Join(' ', requestContext.Scopes)}", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
