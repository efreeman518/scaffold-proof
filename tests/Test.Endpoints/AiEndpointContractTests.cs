using EF.Common.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.AI.Agents;

namespace Test.Endpoints;

/// <summary>
/// No-Aspire HTTP contract tests for AI routes. Fake clients keep endpoint shape, parse guards,
/// and no-write behavior covered without starting the Aspire mesh or a live Foundry provider.
/// </summary>
[TestClass]
public sealed class AiEndpointContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_FakeChatClient_When_ChatCalled_Then_ConfiguredResponseReturned()
    {
        using var factory = CreateAiFactory(_ => "Fake Foundry response.");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new { message = "hello" }, cancellationToken: TestContext.CancellationToken);
        using var payload = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.AreEqual("Fake Foundry response.", payload.RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_FoundryLocalBootstrapFails_When_ChatCalled_Then_NoOpResponseReturned()
    {
        using var factory = new CustomApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:chat"] = string.Empty,
                    ["AiServices:DisableFoundryLocal"] = "false",
                    ["AiServices:LocalWebUrl"] = "not-a-url"
                });
            });
        });
        using var client = factory.CreateClient();

        using var statusResponse = await client.GetAsync("/api/v1/ai/status", TestContext.CancellationToken);
        using var status = await ReadJsonAsync(statusResponse);
        using var chatResponse = await client.PostAsJsonAsync("/api/v1/ai/chat", new { message = "hello" }, cancellationToken: TestContext.CancellationToken);
        using var chat = await ReadJsonAsync(chatResponse);

        Assert.AreEqual(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.AreEqual("none", status.RootElement.GetProperty("provider").GetString());
        Assert.IsFalse(status.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.AreEqual(HttpStatusCode.OK, chatResponse.StatusCode);
        Assert.IsFalse(chat.RootElement.GetProperty("isConfigured").GetBoolean());
        var chatMessage = chat.RootElement.GetProperty("message").GetString();
        Assert.IsNotNull(chatMessage);
        Assert.Contains("not configured", chatMessage);
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_FakeChatClient_When_StreamingChatCalled_Then_EventStreamReturned()
    {
        using var factory = CreateAiFactory(_ => "streamed fake response");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/ai/chat/stream", new { message = "stream" }, cancellationToken: TestContext.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.IsNotNull(mediaType);
        Assert.Contains("text/event-stream", mediaType);
        Assert.Contains("data:", body);
        Assert.Contains("streamed fake response", body);
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_FakeAgent_When_AgentChatCalled_Then_AgentContractReturned()
    {
        using var factory = CreateAiFactory(_ => "unused", new FakeTaskAssistantAgent());
        using var client = factory.CreateClient();
        var conversationId = Guid.NewGuid().ToString("N");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/agent/chat",
            new { message = "help", conversationId }, cancellationToken: TestContext.CancellationToken);
        using var payload = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.AreEqual(conversationId, payload.RootElement.GetProperty("conversationId").GetString());
        Assert.AreEqual("Fake agent response.", payload.RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_FakeChatClient_When_TriageCalledWithApplyFalse_Then_TriageContractReturnedWithoutWrite()
    {
        using var factory = CreateAiFactory(_ =>
            """{"suggestedPriority":"Critical","suggestedCategory":"Incident","confidence":0.91,"rationale":"High impact"}""");
        using var client = factory.CreateClient();
        var taskId = await CreateTaskAsync(client, "Triage contract task", Priority.Low);

        using var response = await client.PostAsync($"/api/v1/ai/triage/{taskId}?apply=false", null, TestContext.CancellationToken);
        using var payload = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.IsFalse(payload.RootElement.GetProperty("applied").GetBoolean());
        Assert.AreEqual("Critical", payload.RootElement.GetProperty("triage").GetProperty("suggestedPriority").GetString());
        Assert.AreEqual(Priority.Low, await ReadTaskPriorityAsync(client, taskId));
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_UnparseableTriage_When_ApplyTrue_Then_ParseGuardReturnsNoWrite()
    {
        using var factory = CreateAiFactory(_ => "not json");
        using var client = factory.CreateClient();
        var taskId = await CreateTaskAsync(client, "Triage parse guard task", Priority.Low);

        using var response = await client.PostAsync($"/api/v1/ai/triage/{taskId}?apply=true", null, TestContext.CancellationToken);
        using var payload = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.IsFalse(payload.RootElement.GetProperty("applied").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, payload.RootElement.GetProperty("triage").ValueKind);
        Assert.AreEqual("Could not parse model output as triage JSON.", payload.RootElement.GetProperty("error").GetString());
        Assert.AreEqual(Priority.Low, await ReadTaskPriorityAsync(client, taskId));
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_FakeChatClient_When_DraftCalled_Then_DraftContractCreatesTask()
    {
        using var factory = CreateAiFactory(_ =>
            """{"description":"Write endpoint AI contract tests.","acceptanceCriteria":"Tests pass"}""");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/ai/tasks/draft",
            new { title = "Draft contract task" }, cancellationToken: TestContext.CancellationToken);
        using var payload = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.IsTrue(payload.RootElement.GetProperty("created").GetBoolean());
        Assert.AreNotEqual(JsonValueKind.Null, payload.RootElement.GetProperty("taskId").ValueKind);
        Assert.AreEqual("Write endpoint AI contract tests.", payload.RootElement.GetProperty("description").GetString());
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_UnparseableDraft_When_DraftCalled_Then_ParseGuardReturnsNoWrite()
    {
        using var factory = CreateAiFactory(_ => "not json");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/ai/tasks/draft",
            new { title = "Draft parse guard task" }, cancellationToken: TestContext.CancellationToken);
        using var payload = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.IsFalse(payload.RootElement.GetProperty("created").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, payload.RootElement.GetProperty("taskId").ValueKind);
        Assert.AreEqual("Could not parse model output.", payload.RootElement.GetProperty("error").GetString());
        Assert.IsFalse(await TaskExistsByTitleAsync(client, "Draft parse guard task"));
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task Given_FakeChatClient_When_NextActionCalled_Then_NextActionContractReturned()
    {
        using var factory = CreateAiFactory(_ => "Work on the highest risk task next.");
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/ai/next-action", null, TestContext.CancellationToken);
        using var payload = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.AreEqual("Work on the highest risk task next.", payload.RootElement.GetProperty("recommendation").GetString());
    }

    private static WebApplicationFactory<Program> CreateAiFactory(
        Func<string, string> reply,
        ITaskAssistantAgent? agent = null)
        => new CustomApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:chat"] = string.Empty,
                    ["AiServices:DisableFoundryLocal"] = "true"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IChatClient>();
                services.AddSingleton<IChatClient>(new RoutingChatClient(reply));

                if (agent is not null)
                {
                    services.RemoveAll<ITaskAssistantAgent>();
                    services.AddSingleton(agent);
                }
            });
        });

    private static async Task<Guid> CreateTaskAsync(HttpClient client, string title, Priority priority)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/task-items",
            new DefaultRequest<TaskItemDto>
            {
                Item = new TaskItemDto { Title = title, Priority = priority }
            });
        var created = await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(JsonOptions);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.IsNotNull(created?.Item?.Id);
        return created.Item.Id.Value;
    }

    private static async Task<Priority> ReadTaskPriorityAsync(HttpClient client, Guid taskId)
    {
        using var response = await client.GetAsync($"/api/v1/task-items/{taskId}");
        var payload = await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(JsonOptions);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(payload?.Item);
        return payload.Item.Priority;
    }

    private static async Task<bool> TaskExistsByTitleAsync(HttpClient client, string title)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/task-items/search",
            new TaskItemCursorSearchRequest
            {
                PageSize = 10,
                Filter = new TaskItemSearchFilter { SearchTerm = title }
            });
        using var payload = await ReadJsonAsync(response);

        return payload.RootElement.GetProperty("data").EnumerateArray()
            .Any(item => string.Equals(item.GetProperty("title").GetString(), title, StringComparison.Ordinal));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"Expected success, got {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");

        return JsonDocument.Parse(body);
    }

    private sealed class RoutingChatClient(Func<string, string> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(GetPrompt(messages)))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply(GetPrompt(messages)));
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static string GetPrompt(IEnumerable<ChatMessage> messages) =>
            string.Join("\n", messages.Select(message => message.Text));
    }

    private sealed class FakeTaskAssistantAgent : ITaskAssistantAgent
    {
        public Task<AgentChatResponse> ChatAsync(
            AgentChatRequest request,
            Guid? tenantId,
            CancellationToken ct = default)
            => Task.FromResult(new AgentChatResponse
            {
                Message = "Fake agent response.",
                ConversationId = request.ConversationId ?? Guid.NewGuid().ToString("N"),
                IsConfigured = true
            });
    }

    public TestContext TestContext { get; set; } = null!;
}
