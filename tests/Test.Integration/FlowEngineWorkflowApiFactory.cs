using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Clients;
using EF.FlowEngine.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Storage;
using TaskFlow.Infrastructure.Storage.CosmosDb;
using Test.Integration.Infrastructure;
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
/// Test seams keep the workflow deterministic while strict lane dependencies remain explicit:
///  - IChatClient -> a fixed reply, so no Foundry/Azure model is needed;
///  - Azure's unavailable Cosmos/Service Bus data planes -> no-op test adapters after valid registration;
///  - the "taskflow-api" self-call connector -> this same in-process server, so workflow writes hit
///    the real endpoints (and therefore the real SQL database) that the tests then assert against.
/// NonAzure uses the assembly's real Redis, RabbitMQ, and SeaweedFS containers.
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
        "FlowEngine__TaskFlowApiBaseUrl",
        "RateLimiting__PerTenant__PermitLimit",
        "Database__Encryption__LocalKeyBase64",
        "Database__Encryption__BlindIndexKeyBase64",
        "Database__Provider",
        "DataProtectionKeysFileUrl",
        "ConnectionStrings__BlobStorage1",
        "ConnectionStrings__TableStorage1",
        "ConnectionStrings__CosmosDb1",
        "ServiceBus1__fullyQualifiedNamespace",
        "ConnectionStrings__Redis1",
        "Storage__S3__ServiceUrl",
        "Storage__S3__PublicServiceUrl",
        "Storage__S3__AccessKeyId",
        "Storage__S3__SecretAccessKey",
        "Messaging__RabbitMq__ConnectionString",
        "ConnectionStrings__MongoDb1",
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
        // No live model and no Service Bus: leave the AI connection empty.
        Environment.SetEnvironmentVariable("ConnectionStrings__chat", string.Empty);
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
        Environment.SetEnvironmentVariable("Database__Provider", TestHostingLane.DatabaseProvider.ToString());
        ConfigureStrictLaneEnvironment();
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

            if (TestHostingLane.Current.Lane == TaskFlow.Hosting.HostingLane.Azure)
            {
                // Cosmos and Service Bus have no component emulator in this lane. Their strict endpoint
                // shapes are validated during registration, then only those two clients are replaced.
                services.RemoveAll<CosmosClient>();
                services.RemoveAll<ITaskViewRepository>();
                services.AddSingleton<ITaskViewRepository, NoOpTaskViewRepository>();
                services.RemoveAll<IIntegrationEventTransport>();
                services.AddSingleton<IIntegrationEventTransport, NoOpEventTransport>();
            }

            // These workflow tests cover engine state and real API/database self-calls. Dedicated transport
            // tests cover Service Bus and RabbitMQ; replace only the broker connector after strict registration
            // so this host does not require the Scheduler-owned RabbitMQ topology or a Service Bus emulator.
            var integrationEventsRegistration = services
                .Where(IsIntegrationEventsClientRegistration)
                .SingleOrDefault()
                ?? throw new InvalidOperationException(
                    "The strict lane test host did not register its integration-events FlowEngine client.");
            services.Remove(integrationEventsRegistration);
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

    private static void ConfigureStrictLaneEnvironment()
    {
        if (TestHostingLane.Current.Lane == TaskFlow.Hosting.HostingLane.Azure)
        {
            var azurite = AzuriteContainerFixture.ConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__BlobStorage1", azurite);
            Environment.SetEnvironmentVariable("ConnectionStrings__TableStorage1", azurite);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__CosmosDb1", "https://taskflow-integration.documents.azure.com:443/");
            Environment.SetEnvironmentVariable(
                "ServiceBus1__fullyQualifiedNamespace", "taskflow-integration.servicebus.windows.net");
            return;
        }

        Environment.SetEnvironmentVariable("ConnectionStrings__Redis1", RedisContainerFixture.ConnectionString);
        Environment.SetEnvironmentVariable("Storage__S3__ServiceUrl", SeaweedFsContainerFixture.ServiceUrl);
        Environment.SetEnvironmentVariable("Storage__S3__PublicServiceUrl", SeaweedFsContainerFixture.ServiceUrl);
        Environment.SetEnvironmentVariable("Storage__S3__AccessKeyId", SeaweedFsContainerFixture.AccessKey);
        Environment.SetEnvironmentVariable("Storage__S3__SecretAccessKey", SeaweedFsContainerFixture.SecretKey);
        Environment.SetEnvironmentVariable(
            "Messaging__RabbitMq__ConnectionString", RabbitMqBrokerFixture.ConnectionString);
        if (TestHostingLane.UsesMongoDb)
            Environment.SetEnvironmentVariable("ConnectionStrings__MongoDb1", MongoDbContainerFixture.ConnectionString);
    }

    private static bool IsIntegrationEventsClientRegistration(ServiceDescriptor descriptor)
    {
        if (descriptor.ServiceType != typeof(IFlowClient)) return false;
        var methodName = descriptor.ImplementationFactory?.Method.Name;
        return methodName?.Contains("AddServiceBusClient", StringComparison.Ordinal) == true
            || methodName?.Contains("AddTaskFlowConnectorClients", StringComparison.Ordinal) == true;
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
