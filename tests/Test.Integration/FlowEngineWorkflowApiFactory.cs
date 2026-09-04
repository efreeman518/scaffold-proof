using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Clients;
using EF.FlowEngine.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Test.Support;
using Test.Support.Hosting;

namespace Test.Integration;

/// <summary>
/// Boots the real TaskFlow.Api host in-process against the standalone SQL container so the two shipped
/// FlowEngine workflows run end-to-end through the engine, its SQL state store, and the public API.
/// Unlike the in-memory endpoint factory this keeps every hosted service running (engine sweep,
/// workflow JSON seeding, startup migrations) - the workflows are driven by the background engine, not
/// a synchronous call - and points all three EF contexts at SQL via connection strings.
///
/// Only three seams are swapped to stay deterministic and offline:
///  - IChatClient -> a fixed reply, so no Foundry/Azure model is needed;
///  - the "integration-events" connector -> an in-memory sink, so no Service Bus is needed;
///  - the "taskflow-api" self-call connector -> this same in-process server, so workflow writes hit
///    the real endpoints (and therefore the real SQL database) that the tests then assert against.
/// </summary>
internal sealed class FlowEngineWorkflowApiFactory : WebApplicationFactory<Program>
{
    // Overrides are pushed through environment variables (set before the host's WebApplication.CreateBuilder
    // runs) because appsettings.Development.json hardcodes a localdb connection string that wins over
    // ConfigureAppConfiguration in the minimal-hosting model. Env vars are read after appsettings, so they win.
    private static readonly string[] OverrideKeys =
    [
        "ASPNETCORE_ENVIRONMENT",
        "ConnectionStrings__TaskFlowDbContextTrxn",
        "ConnectionStrings__TaskFlowDbContextQuery",
        "ConnectionStrings__TaskFlowFlowEngineDbContext",
        "ConnectionStrings__chat",
        "AiServices__DisableFoundryLocal",
        "FlowEngine__TaskFlowApiBaseUrl",
        "RateLimiting__PerTenant__PermitLimit",
        "Database__Encryption__LocalKeyBase64",
        "Database__Encryption__BlindIndexKeyBase64",
        "Database__Provider",
    ];

    private readonly Func<string, string> _chatReply;

    public FlowEngineWorkflowApiFactory(string connectionString, Func<string, string> chatReply)
    {
        _chatReply = chatReply;

        // Development so the host AND Program's own config-driven gates (the migration startup tasks read
        // config["ASPNETCORE_ENVIRONMENT"]) both see Development. Set as an env var so WebApplication.CreateBuilder
        // picks it up; UseEnvironment alone sets the host env but not this config key.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        // All three EF contexts (app trxn/query + FlowEngine state) read these connection strings.
        Environment.SetEnvironmentVariable("ConnectionStrings__TaskFlowDbContextTrxn", connectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__TaskFlowDbContextQuery", connectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__TaskFlowFlowEngineDbContext", connectionString);
        // No live model and no Service Bus: leave the AI connection empty and disable Foundry Local.
        Environment.SetEnvironmentVariable("ConnectionStrings__chat", string.Empty);
        Environment.SetEnvironmentVariable("AiServices__DisableFoundryLocal", "true");
        // Self-call base address; the in-process handler ignores the authority anyway.
        Environment.SetEnvironmentVariable("FlowEngine__TaskFlowApiBaseUrl", "http://localhost");
        // Polling the instance plus the workflow's own self-calls share the per-tenant budget; raise it so
        // the rate limiter never trips during a test (the production default stays 100/min via appsettings).
        Environment.SetEnvironmentVariable("RateLimiting__PerTenant__PermitLimit", "1000000");
        foreach (var (key, value) in TestColumnEncryption.EnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
        // The host must open the same provider as the container the test created the database on.
        Environment.SetEnvironmentVariable("Database__Provider", TestDbProvider.Current.ToString());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development so the migration startup tasks actually run (they are gated to Development/Aspire).
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        });

        builder.ConfigureServices(services =>
        {
            // Deterministic chat: the agent node gets a fixed JSON reply instead of a real model.
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(new FixedChatClient(_chatReply));

            // No Service Bus in tests: stub the message connector the workflows' message nodes use.
            services.AddSingleton<IFlowClient>(new DelegatingMessageClient(
                "integration-events",
                (_, _) => Task.FromResult(new MessageResult { Sent = true, Outcome = DecisionOutcome.Match })));

            // Route the self-call HTTP connector back into this in-process server so workflow-driven
            // writes exercise the real endpoints + SQL. Server is built lazily, after startup, so it is
            // safe to resolve it inside the handler factory (invoked on first self-call at run time).
            services.AddHttpClient("taskflow-api")
                .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        catch (ChannelClosedException)
        {
            // Benign host-teardown race: EF.BackgroundServices' ChannelBackgroundTaskQueue.Dispose calls
            // Complete() on a channel its shutdown handler already completed, so the second Complete throws.
            // It happens only after the host has stopped, so it is safe to ignore here.
        }
        finally
        {
            if (disposing)
                foreach (var key in OverrideKeys)
                    Environment.SetEnvironmentVariable(key, null);
        }
    }

    // Minimal IChatClient returning a fixed reply (the workflow agent node only needs the text back).
    private sealed class FixedChatClient(Func<string, string> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(Prompt(messages)))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply(Prompt(messages)));
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private static string Prompt(IEnumerable<ChatMessage> messages) =>
            string.Join("\n", messages.Select(m => m.Text));
    }
}
