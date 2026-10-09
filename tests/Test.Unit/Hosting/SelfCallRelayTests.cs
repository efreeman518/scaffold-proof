using System.Net;
using System.Runtime.ExceptionServices;
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

    /// <summary>
    /// A node's path or a fetch URL can be absolute or scheme-relative; the token goes to the self-call base address only.
    /// </summary>
    [TestMethod]
    [DataRow("http://evil.example/api/v1/task-items/search")]
    [DataRow("//evil.example/api/v1/task-items/search")]
    [DataRow("http://localhost:8081/api/v1/task-items/search")]
    [DataRow("https://localhost/api/v1/task-items/search")]
    public async Task RelayConfigured_RequestLeavingTheBaseAddress_FailsWithoutSending(string target)
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SendInInstanceAsync(provider, TenantB.ToString(), target: target));

        StringAssert.Contains(thrown.Message, "self-call base address");
        Assert.IsEmpty(transport.Requests, "no token leaves for another address");
    }

    [TestMethod]
    [DataRow("/api/v1/task-items/search")]
    [DataRow("HTTP://LOCALHOST:80/api/v1/task-items/search")]
    public async Task RelayConfigured_RequestToTheBaseAddress_IsSent(string target)
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);

        await SendInInstanceAsync(provider, TenantB.ToString(), target: target);

        Assert.HasCount(1, transport.Requests);
    }

    [TestMethod]
    public async Task RelayConfigured_NodeSuppliedAuthorization_FailsWithoutSending()
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SendInInstanceAsync(
            provider, TenantB.ToString(), request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer node-token")));

        StringAssert.Contains(thrown.Message, "Authorization");
        Assert.IsEmpty(transport.Requests);
    }

    [TestMethod]
    public async Task NodeSuppliedRelayHeaderOnTheContent_IsRemoved()
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, scope: null);

        await SendInInstanceAsync(provider, TenantB.ToString(),
            request => Assert.IsTrue(request.Content!.Headers.TryAddWithoutValidation(RelayHeader, "node-supplied")));

        Assert.IsEmpty(Header(transport.Requests.Single(), RelayHeader));
    }

    /// <summary>
    /// The self-call client over its real transport answers a 302 with the 302: a followed redirect would carry the relay
    /// token and header to the redirect target. A loopback listener answers once, so a second hop would never return.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Client_DoesNotFollowARedirect()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = 0;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
            Interlocked.Increment(ref accepted);
            await using var stream = socket.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, TestContext.CancellationToken);
            var reply = System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/followed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(reply, TestContext.CancellationToken);
        }, TestContext.CancellationToken);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FlowEngine:TaskFlowApiBaseUrl"] = $"http://127.0.0.1:{port}" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterServices.AddTaskFlowApiHttpClient(services.AddFlowEngine(), services, config);
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(RegisterServices.TaskFlowApiClientName);

        using var response = await client.GetAsync(new Uri("/start", UriKind.Relative), TestContext.CancellationToken);
        await server;

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual(1, accepted, "no second hop");
    }

    [TestMethod]
    public void FollowNoRedirects_RefusesAHandlerItCannotConfigure()
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => RegisterServices.FollowNoRedirects(StubHttpMessageHandler.Returns(HttpStatusCode.OK)));
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

    /// <summary>
    /// The package's integration and fetch nodes on the real <c>taskflow-api</c> registration: each self-call they make
    /// runs inside the instance (<c>FlowExecution.Current</c>), so the relay carries that instance's tenant.
    /// </summary>
    [TestMethod]
    public async Task IntegrationAndFetchNodes_RelayTheInstanceTenant()
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);

        var instance = await RunAsync(provider, HttpNodesProbe("/api/v1/task-items/search"), TenantB.ToString());

        Assert.AreEqual("n-done", instance.CurrentNodeId, instance.Error?.Message);
        Assert.HasCount(2, transport.Requests, "one integration call and one fetch call");
        foreach (var request in transport.Requests)
        {
            Assert.AreEqual(TenantB.ToString(), DecodeAsTheApi(request).Single(c => c.Type == "tenant_id").Value);
        }
    }

    /// <summary>
    /// A loop's child workflow inherits the parent's tenant, and its integration node runs inside the child's execution
    /// (<c>FlowExecution.Current</c> is the child), so each child's self-call relays the parent's tenant.
    /// </summary>
    [TestMethod]
    public async Task LoopChildNode_RelaysTheParentsTenant()
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, Scope);
        await provider.GetRequiredService<IWorkflowRegistry>().SaveAsync(LoopChild(), TestContext.CancellationToken);

        var instance = await RunAsync(provider, LoopParent(), TenantB.ToString(),
            new Dictionary<string, ContextValue> { ["items"] = new JsonContextValue { Value = System.Text.Json.JsonSerializer.SerializeToElement(new[] { 1, 2 }) } });

        // The loop suspends while its children run and the parent resumes when they finish, so wait for it to settle.
        var store = provider.GetRequiredService<IExecutionStateStore>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (instance.Status is ExecStatus.Running or ExecStatus.Suspended && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.CancellationToken);
            instance = await store.LoadAsync(instance.InstanceId, TestContext.CancellationToken) ?? instance;
        }

        Assert.AreEqual(ExecStatus.Completed, instance.Status, $"at {instance.CurrentNodeId}: {instance.Error?.Message}");
        Assert.AreEqual("n-done", instance.CurrentNodeId, instance.Error?.Message);
        Assert.HasCount(2, transport.Requests, "one self-call per loop child");
        foreach (var request in transport.Requests)
        {
            Assert.AreEqual(TenantB.ToString(), DecodeAsTheApi(request).Single(c => c.Type == "tenant_id").Value);
        }
    }

    /// <summary>
    /// The package holds the requests its integration and fetch nodes build to the client's base address
    /// (<c>BaseAddressBoundary</c>): a node URL naming another origin is refused before the client pipeline runs, so
    /// nothing is sent and the node takes its Error edge. The relay is off, so the handler checks no address and the
    /// refusal is the package's.
    /// </summary>
    [TestMethod]
    [DataRow("integration", "http://evil.example/api/v1/task-items/search")]
    [DataRow("integration", "//evil.example/api/v1/task-items/search")]
    [DataRow("integration", "http://localhost:8081/api/v1/task-items/search")]
    [DataRow("fetch", "http://evil.example/api/v1/task-items/search")]
    [DataRow("fetch", "//evil.example/api/v1/task-items/search")]
    [DataRow("fetch", "http://localhost:8081/api/v1/task-items/search")]
    public async Task HttpNode_UrlLeavingTheBaseAddress_IsRefusedByThePackage(string nodeType, string target)
    {
        var transport = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var provider = Build(transport, scope: null);
        var definition = nodeType == "integration"
            ? HttpNodesProbe(target)
            : HttpNodesProbe("/api/v1/task-items/search", fetchTarget: target);

        var instance = await RunAsync(provider, definition, TenantB.ToString());

        Assert.AreEqual("n-failed", instance.CurrentNodeId, instance.Error?.Message);
        Assert.HasCount(nodeType == "integration" ? 0 : 1, transport.Requests, "the call leaving the base address is never sent");
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

    /// <summary>
    /// Sends one self-call on the named client from inside a node of an instance with <paramref name="tenant"/> run by the
    /// engine, so <c>FlowExecution.Current</c> is that instance. The probe node builds the request itself, as any other
    /// sender on the named client would, so the handler's own checks are what stand between it and the transport. The
    /// send's exception, if any, is rethrown here.
    /// </summary>
    private async Task SendInInstanceAsync(
        ServiceProvider provider, string? tenant, Action<HttpRequestMessage>? configure = null,
        string target = "http://localhost/api/v1/task-items/search")
    {
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var probe = provider.GetRequiredService<SendProbeExecutor>();
        probe.Work = async ct =>
        {
            using var client = factory.CreateClient(RegisterServices.TaskFlowApiClientName);
            // A path ("/x") or scheme-relative ("//host/x") target is relative to the client's base address, as a node path is.
            var uri = Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? absolute
                : new Uri(target, UriKind.Relative);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent("{}"),
            };
            configure?.Invoke(request);
            using var response = await client.SendAsync(request, ct);
        };

        var instance = await RunAsync(provider, SendProbe(), tenant);

        Assert.IsTrue(probe.Ran, $"the probe node ran: {instance.Error?.Message}");
        probe.Failure?.Throw();
    }

    private async Task<ExecutionInstance> RunAsync(
        ServiceProvider provider, WorkflowDefinition definition, string? tenant, Dictionary<string, ContextValue>? parameters = null)
    {
        await provider.GetRequiredService<IWorkflowRegistry>().SaveAsync(definition, TestContext.CancellationToken);
        return await provider.GetRequiredService<IFlowEngine>().StartAsync(new StartRequest
        {
            WorkflowId = definition.Id,
            TenantId = tenant,
            Params = parameters,
        }, TestContext.CancellationToken);
    }

    private static WorkflowDefinition LoopParent() => WorkflowDefinitionBuilder.FromJson("""
        {
          "id": "relay-loop-parent",
          "version": "1.0.0",
          "status": "Active",
          "entryNodeId": "n-loop",
          "nodes": {
            "n-loop": {
              "id": "n-loop", "type": "loop",
              "config": { "items": "$.params.items", "mode": "sequential", "subWorkflowId": "relay-loop-child" },
              "edges": [ { "on": ["Match"], "nextNodeId": "n-done" }, { "on": ["NoMatch", "Error"], "nextNodeId": "n-failed" } ]
            },
            "n-done": { "id": "n-done", "type": "output", "config": {} },
            "n-failed": { "id": "n-failed", "type": "output", "config": {} }
          }
        }
        """).Build();

    private static WorkflowDefinition LoopChild() => WorkflowDefinitionBuilder.FromJson("""
        {
          "id": "relay-loop-child",
          "version": "1.0.0",
          "status": "Active",
          "entryNodeId": "n-search",
          "nodes": {
            "n-search": {
              "id": "n-search", "type": "integration",
              "retryPolicy": { "maxAttempts": 1 },
              "config": { "clientRef": "taskflow-api", "method": "POST", "path": "/api/v1/task-items/search", "body": { "pageSize": 1 } },
              "edges": [ { "on": ["Match"], "nextNodeId": "n-ok" }, { "on": ["NoMatch", "Error"], "nextNodeId": "n-failed" } ]
            },
            "n-ok": { "id": "n-ok", "type": "output", "config": {} },
            "n-failed": { "id": "n-failed", "type": "output", "config": {} }
          }
        }
        """).Build();

    private static WorkflowDefinition SendProbe() => WorkflowDefinitionBuilder.FromJson($$"""
        {
          "id": "relay-send-probe",
          "version": "1.0.0",
          "status": "Active",
          "entryNodeId": "n-send",
          "nodes": {
            "n-send": {
              "id": "n-send", "type": "{{SendProbeExecutor.Type}}",
              "retryPolicy": { "maxAttempts": 1 },
              "config": {},
              "edges": [ { "on": ["Match", "Error"], "nextNodeId": "n-done" } ]
            },
            "n-done": { "id": "n-done", "type": "output", "config": {} }
          }
        }
        """).Build();

    /// <summary>An integration node then a fetch node, both on the <c>taskflow-api</c> client.</summary>
    private static WorkflowDefinition HttpNodesProbe(string target, string? fetchTarget = null) => WorkflowDefinitionBuilder.FromJson($$"""
        {
          "id": "relay-http-nodes-probe",
          "version": "1.0.0",
          "status": "Active",
          "entryNodeId": "n-integration",
          "nodes": {
            "n-integration": {
              "id": "n-integration", "type": "integration",
              "retryPolicy": { "maxAttempts": 1 },
              "config": { "clientRef": "taskflow-api", "method": "POST", "path": "{{target}}", "body": { "pageSize": 1 } },
              "edges": [ { "on": ["Match"], "nextNodeId": "n-fetch" }, { "on": ["NoMatch", "Error"], "nextNodeId": "n-failed" } ]
            },
            "n-fetch": {
              "id": "n-fetch", "type": "fetch",
              "retryPolicy": { "maxAttempts": 1 },
              "config": { "clientRef": "taskflow-api", "method": "POST", "url": "{{fetchTarget ?? target}}", "body": "{}" },
              "edges": [ { "on": ["Match"], "nextNodeId": "n-done" }, { "on": ["NoMatch", "Error"], "nextNodeId": "n-failed" } ]
            },
            "n-done": { "id": "n-done", "type": "output", "config": {} },
            "n-failed": { "id": "n-failed", "type": "output", "config": {} }
          }
        }
        """).Build();

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
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FlowEngine:SelfCall:TokenScope"] = scope,
                ["FlowEngine:TaskFlowApiBaseUrl"] = "http://localhost",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TokenCredential>(new FixedCredential());
        var probe = new SendProbeExecutor();
        services.AddSingleton(probe);
        var fe = services.AddFlowEngine().UseAllInMemoryProviders().AddNodeExecutor(probe);
        RegisterServices.AddTaskFlowApiHttpClient(fe, services, config).ConfigurePrimaryHttpMessageHandler(() => transport);
        return services.BuildServiceProvider();
    }

    /// <summary>Runs <see cref="Work"/> as the node's work and keeps the exception it throws for the test to rethrow.</summary>
    private sealed class SendProbeExecutor : INodeExecutor
    {
        public const string Type = "relay-send-probe";

        public Func<CancellationToken, Task> Work { get; set; } = _ => Task.CompletedTask;

        public bool Ran { get; private set; }

        public ExceptionDispatchInfo? Failure { get; private set; }

        public string NodeType => Type;

        public async Task<NodeResult> ExecuteAsync(NodeDefinition node, ExecutionInstance instance, IExecutionContext ctx, CancellationToken ct = default)
        {
            Ran = true;
            try
            {
                await Work(ct);
            }
            catch (Exception ex)
            {
                Failure = ExceptionDispatchInfo.Capture(ex);
                throw;
            }

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
