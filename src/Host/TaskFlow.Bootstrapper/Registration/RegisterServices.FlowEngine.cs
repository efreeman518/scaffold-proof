using EF.AspNetCore.RequestContext;
using EF.Auth.Relay;
using EF.Auth.Tokens;
using EF.FlowEngine;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.AdminApi;
using EF.FlowEngine.Clients;
using EF.FlowEngine.Clients.AI;
using EF.FlowEngine.Clients.Http;
using EF.FlowEngine.Clients.ServiceBus;
using EF.FlowEngine.Model;
using EF.FlowEngine.Sql;
using EF.Host;
using EF.Messaging.RabbitMq;
using EF.Messaging.Tracing;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Infrastructure.Repositories;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures register services host behavior for TaskFlow runtime services.</summary>
public static partial class RegisterServices
{
    // FlowEngine wiring - engine runtime + connector clients + JSON workflow seeding.
    // The 19 built-in node executors are auto-registered by AddFlowEngine().
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
            .UseCircuitBreakerSql<TaskFlowFlowEngineDbContext>()
            // D-075: document nodes read attachment evidence from the lane's object storage (Azure Blob or S3).
            .UseDocumentStore<AttachmentDocumentStore>();

        // Retention removes terminal workflow instances so the FlowEngine state store stays bounded.
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
        AddTaskFlowApiHttpClient(fe, services, config);

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
    /// The workflow self-call client. The node <c>retryPolicy</c> is the only retry owner, so
    /// <c>AddResilientHttpClient</c> replaces the inherited ServiceDefaults handler (D-063) on this dedicated named
    /// client with the package pipeline: one send per node attempt for every method, with the timeouts and circuit
    /// breaker kept. The adapter creates the named client from the factory per request, so a workflow can call the
    /// host that runs it. <see cref="SelfCallRelayHandler"/> runs inside that pipeline, once per attempt, and relays the
    /// instance tenant when <c>FlowEngine:SelfCall:TokenScope</c> is set (D-068). The If-Match: * trusted-automation
    /// override (D-032) travels in each PATCH node's own "headers" config (FlowEngine forwards
    /// IntegrationNodeConfig.Headers). The client follows no redirect: the transport would send the relay token and
    /// header to the redirect target below the handler's base-address check, and no self-call address redirects.
    /// </summary>
    internal static IHttpClientBuilder AddTaskFlowApiHttpClient(FlowEngineBuilder fe, IServiceCollection services, IConfiguration config)
    {
        var apiBaseUrl = config["FlowEngine:TaskFlowApiBaseUrl"]
            ?? config["Gateway:BaseUrl"]
            ?? "https://localhost";
        var baseAddress = new Uri(apiBaseUrl);
        AddSelfCallRelay(services, config, baseAddress);
        var client = services.AddHttpClient(TaskFlowApiClientName, c => c.BaseAddress = baseAddress);
        fe.AddResilientHttpClient(TaskFlowApiClientName, TaskFlowApiClientName);
        return client
            .ConfigurePrimaryHttpMessageHandler((handler, _) => FollowNoRedirects(handler))
            .AddHttpMessageHandler<SelfCallRelayHandler>();
    }

    /// <summary>Turns automatic redirects off on the self-call client's primary handler, whichever handler type it is.</summary>
    internal static void FollowNoRedirects(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.AllowAutoRedirect = false;
                break;
            case HttpClientHandler client:
                client.AllowAutoRedirect = false;
                break;
            default:
                throw new InvalidOperationException(
                    $"The {TaskFlowApiClientName} client's primary handler is a {handler.GetType().Name}; redirects cannot be turned off on it.");
        }
    }

    /// <summary>
    /// The self-call relay settings and their token source. <c>ForwardedClaims</c> is bound into a named instance so the
    /// Api's own instance (its relay transformation) is bound once. With the relay configured, host start fails when the
    /// allowlist would drop a relayed claim type. Tokens come from <c>EF.Auth</c> <see cref="AccessTokenCache"/> over the
    /// host credential (<c>ManagedIdentityClientId</c>, <c>AzureTenantId</c>), as the Gateway acquires its tokens.
    /// </summary>
    private static void AddSelfCallRelay(IServiceCollection services, IConfiguration config, Uri baseAddress)
    {
        services.AddOptions<ForwardedClaimsOptions>(SelfCallRelayOptions.ForwardedClaimsOptionsName)
            .Bind(config.GetSection(ForwardedClaimsOptions.ConfigSectionName));
        services.AddOptions<SelfCallRelayOptions>()
            .Bind(config.GetSection(SelfCallRelayOptions.ConfigSectionName))
            .Configure(relay => relay.ApiBaseAddress = baseAddress)
            .Validate<IOptionsMonitor<ForwardedClaimsOptions>, IOptions<HttpRequestContextOptions>>(
                (relay, claims, requestContext) => !relay.IsRelayConfigured || SelfCallRelayHandler.DroppedClaimTypes(
                    claims.Get(SelfCallRelayOptions.ForwardedClaimsOptionsName), requestContext.Value).Count == 0,
                $"{SelfCallRelayOptions.ConfigSectionName}:TokenScope is set, so ForwardedClaims:ClaimTypes must list every " +
                "relayed claim type: the tenant claim, sub, and the .NET name and role claim types.")
            .ValidateOnStart();
        services.AddAzureTokenCredential(config);
        services.AddAccessTokenCache();
        services.AddTransient<SelfCallRelayHandler>();
    }

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
