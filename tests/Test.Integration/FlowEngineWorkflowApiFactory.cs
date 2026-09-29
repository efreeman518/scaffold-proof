using EF.AI.Testing;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Clients;
using EF.FlowEngine.Model;
using EF.Testing.Environment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Bootstrapper;
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
    // runs) because Program reads configuration while registering services, before ConfigureAppConfiguration
    // sources apply in the minimal-hosting model. Env vars are read after appsettings, so they win.
    // The scope restores each variable's original value on dispose, so a value set in the shell survives.
    private readonly EnvironmentVariableScope _environment = new();

    private readonly Func<string, string> _chatReply;

    public FlowEngineWorkflowApiFactory(string connectionString, Func<string, string> chatReply)
    {
        _chatReply = chatReply;

        // Development so the host AND Program's own config-driven gates (the migration startup tasks read
        // config["ASPNETCORE_ENVIRONMENT"]) both see Development. Set as an env var so WebApplication.CreateBuilder
        // picks it up; UseEnvironment alone sets the host env but not this config key.
        _environment.Set("ASPNETCORE_ENVIRONMENT", "Development");
        // All three EF contexts (app trxn/query + FlowEngine state) read these connection strings.
        _environment.Set("ConnectionStrings__TaskFlowDbContextTrxn", connectionString);
        _environment.Set("ConnectionStrings__TaskFlowDbContextQuery", connectionString);
        _environment.Set("ConnectionStrings__TaskFlowFlowEngineDbContext", connectionString);
        // No live model and no Service Bus: leave the AI connection empty.
        _environment.Set("ConnectionStrings__chat", string.Empty);
        // Self-call base address; the in-process handler ignores the authority anyway.
        _environment.Set("FlowEngine__TaskFlowApiBaseUrl", "http://localhost");
        // Polling the instance plus the workflow's own self-calls share the per-tenant budget; raise it so
        // the rate limiter never trips during a test (the production default stays 100/min via appsettings).
        _environment.Set("RateLimiting__PerTenant__PermitLimit", "1000000");
        foreach (var (key, value) in TestColumnEncryption.EnvironmentVariables)
        {
            _environment.Set(key, value);
        }
        // The host must open the same provider as the container the test created the database on.
        _environment.Set("Database__Provider", TestHostingLane.DatabaseProvider.ToString());
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
            services.AddSingleton<IChatClient>(new FakeChatClient(call => _chatReply(call.PromptText)));

            if (TestHostingLane.Current.Lane == TaskFlow.Hosting.HostingLane.Azure)
            {
                // Cosmos and Service Bus have no component emulator in this lane. Their strict endpoint
                // shapes are validated during registration, then only those two clients are replaced.
                services.RemoveAll<CosmosClient>();
                services.RemoveAll<ITaskViewRepository>();
                services.AddSingleton<ITaskViewRepository, NoOpTaskViewRepository>();
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

    private void ConfigureStrictLaneEnvironment()
    {
        if (TestHostingLane.Current.Lane == TaskFlow.Hosting.HostingLane.Azure)
        {
            var azurite = AzuriteContainerFixture.ConnectionString;
            _environment.Set("ConnectionStrings__BlobStorage1", azurite);
            _environment.Set("ConnectionStrings__TableStorage1", azurite);
            _environment.Set(
                "ConnectionStrings__CosmosDb1", "https://taskflow-integration.documents.azure.com:443/");
            _environment.Set(
                "ServiceBus1__fullyQualifiedNamespace", "taskflow-integration.servicebus.windows.net");
            return;
        }

        _environment.Set("ConnectionStrings__Redis1", RedisContainerFixture.ConnectionString);
        _environment.Set("Storage__S3__ServiceUrl", SeaweedFsContainerFixture.ServiceUrl);
        _environment.Set("Storage__S3__PublicServiceUrl", SeaweedFsContainerFixture.ServiceUrl);
        _environment.Set("Storage__S3__AccessKeyId", SeaweedFsContainerFixture.AccessKey);
        _environment.Set("Storage__S3__SecretAccessKey", SeaweedFsContainerFixture.SecretKey);
        _environment.Set(
            "Messaging__RabbitMq__ConnectionString", RabbitMqBrokerFixture.ConnectionString);
        if (TestHostingLane.UsesMongoDb)
            _environment.Set("ConnectionStrings__MongoDb1", MongoDbContainerFixture.ConnectionString);
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
        finally
        {
            if (disposing)
                _environment.Dispose();
        }
    }
}
