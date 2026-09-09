using EF.FlowEngine;
using EF.FlowEngine.AdminApi;
using EF.FlowEngine.Clients.AI;
using EF.FlowEngine.Clients.Http;
using EF.FlowEngine.Clients.ServiceBus;
using EF.FlowEngine.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures register services host behavior for TaskFlow runtime services.</summary>
public static partial class RegisterServices
{
    // FlowEngine wiring - engine runtime + connector clients + JSON workflow seeding.
    // The 19 built-in node executors are auto-registered by AddFlowEngine() in this version.
    // Engine state + outbox live in TaskFlowFlowEngineDbContext (separate schema, shared SQL connection).
    // The Dashboard + Designer live in TaskFlow.Blazor and call into MapFlowEngineAdmin via the gateway.
    /// <summary>
    /// Wires FlowEngine runtime state, locks, registry, human tasks, outbox, circuit breaker,
    /// connector clients, JSON workflow seeding, and admin policies. It is registered after AI
    /// services so agent connectors can resolve the Aspire-wired chat client.
    /// </summary>
    private static void AddFlowEngineServices(IServiceCollection services, IConfiguration config)
    {
        var fe = services.AddFlowEngine(options =>
        {
            options.DefaultLeaseDuration = TimeSpan.FromSeconds(30);
            options.LeaseRenewalInterval = TimeSpan.FromSeconds(13);
            options.SweepInterval = TimeSpan.FromSeconds(30);
            options.SweepBatchSize = 50;
        })
            .UseStateStoreSql<TaskFlowFlowEngineDbContext>()
            .UseLockProviderSql<TaskFlowFlowEngineDbContext>()
            .UseWorkflowRegistrySql<TaskFlowFlowEngineDbContext>()
            .UseHumanTaskStoreSql<TaskFlowFlowEngineDbContext>()
            .UseOutboxSql<TaskFlowFlowEngineDbContext>()
            .UseCircuitBreakerSql<TaskFlowFlowEngineDbContext>();

        // Terminal workflow instances were never removed, so the FlowEngine state store grew forever.
        // UseRetentionPolicy registers a hosted service, and every host loading this assembly would run its
        // own copy against the same tables; Scheduling:OwnsRetention makes the Scheduler the single owner.
        if (config.GetValue("Scheduling:OwnsRetention", false))
        {
            fe.UseRetentionPolicy(new RetentionPolicy
            {
                MaxAge = TimeSpan.FromDays(7),
                Statuses = [ExecStatus.Completed, ExecStatus.Faulted, ExecStatus.Cancelled],
                RunInterval = TimeSpan.FromHours(6)
            });
        }

        AddTaskFlowConnectorClients(fe, services, config);
        AddWorkflowJsonSeeding(fe);

        services.AddFlowEngineAdminPolicies();
        services.AddFlowEngineAdminTenantPolicy(options =>
        {
            if (string.Equals(config["AuthMode"] ?? "Scaffold", "Scaffold", StringComparison.OrdinalIgnoreCase))
            {
                options.TenantClaimType = "flowengine_tenant_id";
                options.RequireTenant = false;
                return;
            }

            options.TenantClaimType = "tenant_id";
        });
    }

    /// <summary>
    /// Registers external connectors used by workflow nodes. Self-call HTTP goes through the public
    /// API to preserve auth, validation, audit, and integration events; Service Bus shares the app
    /// event connection; the agent client uses the shared IChatClient for local and Azure models.
    /// </summary>
    private static void AddTaskFlowConnectorClients(
        FlowEngineBuilder fe,
        IServiceCollection services,
        IConfiguration config)
    {
        // Self-call client - workflows that mutate TaskItems do so through the public API
        // (preserves auth, validation, audit, integration-event publishing).
        var apiBaseUrl = config["FlowEngine:TaskFlowApiBaseUrl"]
            ?? config["Gateway:BaseUrl"]
            ?? "https://localhost";
        services.AddTransient<FlowEngineIfMatchOverrideHandler>();
        services.AddHttpClient("taskflow-api", c => c.BaseAddress = new Uri(apiBaseUrl))
            .AddHttpMessageHandler<FlowEngineIfMatchOverrideHandler>();
        fe.AddResilientHttpClient("taskflow-api", namedClient: "taskflow-api");

        // Service Bus message client - uses the same connection string as the application's
        // integration event publisher. Workflow `message` nodes publish through this; the
        // existing FunctionServiceBusTrigger picks them up alongside domain events.
        var sbConnStr = ResolveConnectionString(config, "ServiceBus1", "Values:ServiceBus1");
        if (!string.IsNullOrEmpty(sbConnStr))
        {
            var topic = config["FlowEngine:ServiceBusTopic"] ?? "taskflow-integration-events";
            fe.AddServiceBusClient("integration-events", sbConnStr, topic);
        }

        // Agent workflow nodes share the same host-provided IChatClient as the rest of the AI demos.
        fe.AddChatClientAgentClient(
            clientRef: "ai-agent",
            chatClientFactory: sp => sp.GetRequiredService<IChatClient>());
    }

    // JSON workflow definitions live in TaskFlow.Api/Workflows/. The seeding service is a
    // hosted service that runs once at startup, skipping the directory if it does not exist
    // (e.g. when this assembly is loaded by TaskFlow.Functions or TaskFlow.Scheduler).
    // Replaces the bespoke WorkflowSeedStartupTask used by older local wiring.
    /// <summary>
    /// Seeds workflow definitions from TaskFlow.Api/Workflows when that directory is present in
    /// the running host output. Other hosts can load this assembly without requiring workflow files.
    /// </summary>
    private static void AddWorkflowJsonSeeding(FlowEngineBuilder fe)
    {
        fe.AddWorkflowJsonSeeding(opts =>
        {
            opts.Directory = "Workflows";
            opts.SearchPattern = "*.json";
            opts.ActivateOnSeed = true;
            opts.OverwriteExistingVersion = false;
        });
    }
}
