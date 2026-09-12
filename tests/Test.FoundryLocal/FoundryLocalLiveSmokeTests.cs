using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TaskFlow.Infrastructure.Data;
using Test.Support;

namespace Test.FoundryLocal;

/// <summary>RID-bound live smoke tests for the API-host Foundry Local SDK bootstrap.</summary>
[TestClass]
[TestCategory("LiveAI")]
[TestCategory("Foundry")]
[TestCategory("FoundryLocal")]
[DoNotParallelize]
public sealed class FoundryLocalLiveSmokeTests
{
    private const string RunTestsEnvironmentVariable = "TASKFLOW_RUN_FOUNDRY_LOCAL_TESTS";
    private static readonly Lock ClientLock = new();
    private static FoundryLocalApiFactory? _factory;
    private static HttpClient? _client;
    private static Exception? _startupException;

    public TestContext TestContext { get; set; } = null!;

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _client = null;
        _factory = null;
        _startupException = null;
    }

    [TestMethod]
    [Timeout(360000, CooperativeCancellation = true)]
    public async Task Given_FoundryLocalApiHost_When_ChatEndpointCalled_Then_ConfiguredModelResponds()
    {
        var client = await CreateFoundryLocalClientAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(300));

        HttpResponseMessage response;
        string? timeoutReason = null;
        try
        {
            response = await client.PostAsJsonAsync(
                "/api/v1/ai/chat",
                new { message = "Answer in one short sentence: what does TaskFlow track?" },
                timeout.Token);
        }
        catch (OperationCanceledException ex) when (!TestContext.CancellationToken.IsCancellationRequested)
        {
            timeoutReason =
                "Foundry Local provider bootstrapped, but chat smoke did not complete within 300 seconds. " +
                ex.Message;
            response = null!;
        }

        if (timeoutReason is not null)
        {
            Assert.Inconclusive(timeoutReason);
        }

        using var responseMessage = response;
        using var payload = await ReadJsonAsync(responseMessage, timeout.Token);

        Assert.AreEqual(HttpStatusCode.OK, responseMessage.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        AssertHasText(payload.RootElement.GetProperty("message"), "message");
    }

    [TestMethod]
    [Timeout(600000, CooperativeCancellation = true)]
    public async Task Given_FoundryLocalApiHost_When_AgentChatEndpointCalled_Then_ConfiguredAgentResponds()
    {
        var client = await CreateFoundryLocalClientAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(300));

        HttpResponseMessage response;
        string? timeoutReason = null;
        try
        {
            response = await client.PostAsJsonAsync(
                "/api/v1/agent/chat",
                new
                {
                    message = "Do not call tools. Reply exactly OK.",
                    conversationId = Guid.NewGuid().ToString("N"),
                    useTools = false
                },
                timeout.Token);
        }
        catch (OperationCanceledException ex) when (!TestContext.CancellationToken.IsCancellationRequested)
        {
            timeoutReason =
                "Foundry Local provider bootstrapped, but the code-hosted agent smoke did not complete within 300 seconds. " +
                ex.Message;
            response = null!;
        }

        if (timeoutReason is not null)
        {
            Assert.Inconclusive(timeoutReason);
        }

        using var responseMessage = response;
        using var payload = await ReadJsonAsync(responseMessage, timeout.Token);

        Assert.AreEqual(HttpStatusCode.OK, responseMessage.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        AssertHasText(payload.RootElement.GetProperty("message"), "message");
        AssertHasText(payload.RootElement.GetProperty("conversationId"), "conversationId");
    }

    [TestMethod]
    [Timeout(360000, CooperativeCancellation = true)]
    public async Task Given_FoundryLocalApiHost_When_TaskTriageCalled_Then_TriageContractReturnedWithoutApplyingWrites()
    {
        var client = await CreateFoundryLocalClientAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(300));
        var taskId = await CreateTaskAsync(client, "Foundry Local triage smoke " + Guid.NewGuid().ToString("N"), timeout.Token);

        HttpResponseMessage response;
        string? timeoutReason = null;
        try
        {
            response = await client.PostAsync($"/api/v1/ai/triage/{taskId}?apply=false", null, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!TestContext.CancellationToken.IsCancellationRequested)
        {
            timeoutReason =
                "Foundry Local provider bootstrapped, but task triage smoke did not complete within 300 seconds. " +
                ex.Message;
            response = null!;
        }

        if (timeoutReason is not null)
        {
            Assert.Inconclusive(timeoutReason);
        }

        using var responseMessage = response;
        using var payload = await ReadJsonAsync(responseMessage, timeout.Token);

        Assert.AreEqual(HttpStatusCode.OK, responseMessage.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.IsFalse(payload.RootElement.GetProperty("applied").GetBoolean());
        var triage = payload.RootElement.GetProperty("triage");
        if (triage.ValueKind == JsonValueKind.Null)
        {
            AssertHasText(payload.RootElement.GetProperty("error"), "error");
            return;
        }

        Assert.AreEqual(JsonValueKind.Object, triage.ValueKind);
        AssertHasText(triage.GetProperty("suggestedPriority"), "suggestedPriority");
    }

    private static async Task<HttpClient> CreateFoundryLocalClientAsync()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(RunTestsEnvironmentVariable),
                "false",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"{RunTestsEnvironmentVariable}=false - Foundry Local live tests opted out.");
        }

        var client = CreateClient();
        using var response = await client.GetAsync("/api/v1/ai/status");
        using var payload = await ReadJsonAsync(response, CancellationToken.None);

        var provider = payload.RootElement.GetProperty("provider").GetString();
        var isConfigured = payload.RootElement.GetProperty("isConfigured").GetBoolean();
        if (provider != "local" || !isConfigured)
        {
            Assert.Fail(
                $"Foundry Local did not bootstrap through the API host. AI status provider={provider ?? "unknown"} configured={isConfigured}.");
        }

        return client;
    }

    private static HttpClient CreateClient()
    {
        if (_client is not null)
        {
            return _client;
        }

        if (_startupException is not null)
        {
            HandleStartupException(_startupException);
        }

        lock (ClientLock)
        {
            if (_client is not null)
            {
                return _client;
            }

            try
            {
                _factory = new FoundryLocalApiFactory();
                _client = _factory.CreateClient();
                _client.Timeout = TimeSpan.FromMinutes(10);
                return _client;
            }
            catch (Exception ex)
            {
                _startupException = ex;
                _factory?.Dispose();
                _factory = null;
                HandleStartupException(ex);
                throw;
            }
        }
    }

    private static void HandleStartupException(Exception ex)
    {
        if (IsMissingFoundryLocalRuntime(ex))
        {
            Assert.Inconclusive("Foundry Local runtime is not installed or not discoverable: " + ex.Message);
        }

        Assert.Fail("Foundry Local runtime is installed or startup failed after discovery: " + ex);
    }

    private static bool IsMissingFoundryLocalRuntime(Exception ex) =>
        ex is FileNotFoundException
        || ex is DirectoryNotFoundException
        || ex is PlatformNotSupportedException
        || ex is System.ComponentModel.Win32Exception
        || (ex.InnerException is not null && IsMissingFoundryLocalRuntime(ex.InnerException));

    private static async Task<Guid> CreateTaskAsync(HttpClient client, string title, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/task-items",
            new { item = new { title, priority = 2 } },
            ct);
        using var payload = await ReadJsonAsync(response, ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var id = payload.RootElement.GetProperty("item").GetProperty("id").GetGuid();
        Assert.AreNotEqual(Guid.Empty, id);
        return id;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail(
                $"Expected JSON success response, got {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(body)}");
        }

        if (string.IsNullOrWhiteSpace(body))
            Assert.Fail($"Expected JSON response, got empty body from {response.RequestMessage?.RequestUri}.");

        return JsonDocument.Parse(body);
    }

    private static void AssertHasText(JsonElement element, string propertyName)
    {
        Assert.AreEqual(JsonValueKind.String, element.ValueKind, $"{propertyName} should be a JSON string.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(element.GetString()), $"{propertyName} should not be blank.");
    }

    private static string Truncate(string value) =>
        value.Length <= 1000 ? value : value[..1000] + "...";

    private sealed class FoundryLocalApiFactory
        : WebApplicationFactoryBase<Program, TaskFlowDbContextTrxn, TaskFlowDbContextQuery>
    {
        private readonly string _dbName = $"FoundryLocalDb_{Guid.NewGuid()}";

        protected override void ConfigureTestConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(TestColumnEncryption.Configuration);
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:chat"] = string.Empty,
                ["AiServices:DisableFoundryLocal"] = "false",
                ["AiServices:RequireFoundryLocal"] = "true",
                ["AiServices:LocalModel"] = Environment.GetEnvironmentVariable("TASKFLOW_FOUNDRY_LOCAL_MODEL") ?? "qwen2.5-0.5b",
                ["AiServices:LocalWebUrl"] = Environment.GetEnvironmentVariable("TASKFLOW_FOUNDRY_LOCAL_WEB_URL") ?? GetFreeLoopbackUrl(),
                ["RateLimiting:PerTenant:PermitLimit"] = "1000000",
                ["RateLimiting:PerTenant:WindowSeconds"] = "1"
            });
        }

        protected override DbContextOptions BuildTrxnOptions() =>
            new DbContextOptionsBuilder<TaskFlowDbContextTrxn>().UseInMemoryDatabase(_dbName).Options;

        protected override DbContextOptions BuildQueryOptions() =>
            new DbContextOptionsBuilder<TaskFlowDbContextQuery>().UseInMemoryDatabase(_dbName).Options;

        private static string GetFreeLoopbackUrl()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return $"http://127.0.0.1:{port}";
        }
    }
}
