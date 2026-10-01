using EF.FlowEngine;
using EF.FlowEngine.AdminApi;
using EF.FlowEngine.Clients;
using EF.FlowEngine.Clients.AI;
using EF.FlowEngine.Clients.Http;
using EF.FlowEngine.Clients.ServiceBus;
using EF.FlowEngine.Model;
using EF.Host;
using EF.Messaging.RabbitMq;
using EF.Messaging.Tracing;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Infrastructure.Messaging.RabbitMq;

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
        // The If-Match: * trusted-automation override (D-032) travels in each PATCH node's own
        // "headers" config (EF.FlowEngine 1.0.173 forwards IntegrationNodeConfig.Headers), so this
        // client needs no message handler of its own.
        AddTaskFlowApiHttpClient(services, apiBaseUrl);
        fe.AddClient(CreateTaskFlowApiFlowClient);

        if (ResolveMessagingProvider(config) == MessagingProvider.RabbitMq)
        {
            // Workflow messages are topic publications and may intentionally have no subscriber. The package
            // publisher publishes mandatory:false, so zero bindings is accepted just like a Service Bus topic
            // with zero subscriptions, and awaits the broker confirm under PublisherConfirmTimeout.
            fe.AddClient(sp => CreateRabbitMqFlowEngineMessageClient(sp.GetRequiredService<IRabbitMqPublisher>()));
        }
        else
        {
            // Service Bus message client - reuses the application's named client for either a local
            // connection string or deployed managed identity. AddServiceBusServices has already enforced
            // the strict Azure requirement that one of those connection forms exists.
            var sbConnStr = config.ResolveConnection("ServiceBus1", "Values:ServiceBus1");
            var fullyQualifiedNamespace = ResolveServiceBusFullyQualifiedNamespace(config);
            if (!string.IsNullOrEmpty(sbConnStr) || !string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
            {
                var topic = ResolveFlowEngineServiceBusTopic(config);
                fe.AddServiceBusClient(
                    "integration-events",
                    sp => sp.GetRequiredService<IAzureClientFactory<ServiceBusClient>>()
                        .CreateClient("TaskFlowSBClient"),
                    topic);
            }
        }

        // Agent workflow nodes share the same host-provided IChatClient as the rest of the AI demos.
        fe.AddChatClientAgentClient(
            clientRef: "ai-agent",
            chatClientFactory: sp => sp.GetRequiredService<IChatClient>());
    }

    internal const string TaskFlowApiClientName = "taskflow-api";

    /// <summary>
    /// The workflow self-call client. The node <c>retryPolicy</c> is the only retry owner (EF.FlowEngine 1.0.199),
    /// so this client sends once per node attempt for every method and keeps the standard timeouts and circuit
    /// breaker. The inherited ServiceDefaults handler (D-063) still retries safe methods and shares one options
    /// instance across clients, so it is replaced here, not reconfigured; <c>RemoveAllResilienceHandlers</c> is
    /// experimental (the Blazor gRPC read client sets the precedent), so remove the suppression when it is not.
    /// <para>
    /// shortcut: the <c>ResilientHttpFlowClient</c> adapter is registered over this handler instead of through
    /// <c>AddResilientHttpClient</c>, because 1.0.199 sets <c>HttpStandardResilienceOptions.Retry</c> to null
    /// there, and the name-agnostic options validator any <c>AddStandardResilienceHandler</c> registers
    /// (ServiceDefaults) then fails host start ("The taskflow-api.Retry field is required").
    /// <c>AddDirectHttpClient</c> is no substitute: it captures one HttpClient when the engine resolves its
    /// clients. Return to <c>AddResilientHttpClient</c> once the package coexists with that validator.
    /// </para>
    /// </summary>
    internal static IHttpClientBuilder AddTaskFlowApiHttpClient(IServiceCollection services, string apiBaseUrl)
    {
        var client = services.AddHttpClient(TaskFlowApiClientName, c => c.BaseAddress = new Uri(apiBaseUrl));
#pragma warning disable EXTEXP0001
        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        client.AddStandardResilienceHandler(o => o.Retry.ShouldHandle = static _ => ValueTask.FromResult(false));
        return client;
    }

    /// <summary>Per-call HttpClient from the factory, so handler rotation and the lazy test-server handler both hold.</summary>
    private static EF.FlowEngine.Abstractions.IFlowClient CreateTaskFlowApiFlowClient(IServiceProvider sp) =>
        new ResilientHttpFlowClient(TaskFlowApiClientName, sp.GetRequiredService<IHttpClientFactory>(), TaskFlowApiClientName);

    internal static string ResolveFlowEngineServiceBusTopic(IConfiguration config) =>
        config["FlowEngine:ServiceBusTopic"]
        ?? config["DomainEventsTopic"]
        ?? TaskFlowIntegrationEvents.Destination;

    /// <summary>
    /// FlowEngine "integration-events" client over the package publisher. A timeout or broker refusal surfaces as
    /// <see cref="RabbitMqPublishException"/> (the primary failure as its inner exception); caller cancellation
    /// stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    internal static DelegatingMessageClient CreateRabbitMqFlowEngineMessageClient(IRabbitMqPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        return new DelegatingMessageClient(
            "integration-events",
            async (request, ct) =>
            {
                if (request.DeliveryMode != MessageDeliveryMode.FireAndForget)
                {
                    throw new NotSupportedException(
                        "The RabbitMQ integration-events client supports only fire-and-forget workflow messages.");
                }

                var messageId = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? Guid.CreateVersion7().ToString()
                    : request.IdempotencyKey;
                var message = CreateRabbitMqFlowEngineMessage(request, messageId, out var publishActivity);
                using (publishActivity)
                {
                    await publisher.PublishAsync(TaskFlowRabbitMqTopology.Exchange, message, ct).ConfigureAwait(false);
                }

                return new MessageResult
                {
                    Sent = true,
                    MessageId = messageId,
                    CorrelationId = request.CorrelationId,
                    Outcome = DecisionOutcome.Match
                };
            });
    }

    /// <summary>
    /// Maps one FlowEngine request to the shared RabbitMQ wire shape and starts its D-053 producer span.
    /// The caller owns the returned activity and must keep it alive through the confirmed publish.
    /// </summary>
    internal static RabbitMqMessage CreateRabbitMqFlowEngineMessage(
        MessageRequest request,
        string messageId,
        out System.Diagnostics.Activity? publishActivity)
    {
        var binaryBody = request.BinaryBody;
        var body = binaryBody ?? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request.Body);
        var contentType = request.ContentType
            ?? (binaryBody is null ? "application/json" : "application/octet-stream");
        var headers = request.Properties?.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal)
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);

        publishActivity = MessagingActivitySource.StartSend(
            MessagingActivitySource.RabbitMqSystem, TaskFlowRabbitMqTopology.Exchange, messageId);
        // A null span (nothing listening) injects the ambient context, so the consumer still joins the trace.
        MessagingTraceContext.Inject(publishActivity, (key, value) => headers[key] = value);

        return new RabbitMqMessage(
            body,
            RoutingKey: request.Subject,
            MessageId: messageId,
            ContentType: contentType,
            CorrelationId: request.CorrelationId,
            Headers: headers);
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
