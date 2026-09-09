using AppHost;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

// Testing environment: set by test classes before calling DistributedApplicationTestingBuilder.
// Keep test runs isolated and trim only resources that are too heavy or need external tools.
var isTesting = Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_TESTING") == "true"
    || string.Equals(builder.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);
var applicationStyle = Environment.GetEnvironmentVariable("TASKFLOW_APPLICATION_STYLE");
var functionsAvailableInTesting =
    Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_FUNCTIONS_AVAILABLE") == "true";
var reactAvailableInTesting =
    Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_REACT_AVAILABLE") == "true";
var unoWasmAvailableInTesting =
    Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_UNO_WASM_AVAILABLE") == "true";
// The Scheduler owns the outbox dispatcher, so the messaging mesh test needs it in the graph. It is opt-in
// under test for the same reason Functions and the SPAs are: one more host to boot inside the startup budget.
var schedulerAvailableInTesting =
    Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE") == "true";
// Opt-in only: normal Aspire tests keep AI deterministic by disabling Foundry Local.
// Set before AppHost build only for manual local-AI AppHost runs.
var foundryLocalAvailableInTesting =
    Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_ENABLE_FOUNDRY_LOCAL") == "true";

// Keep the database password stable across restarts so persistent volumes remain usable.
// Tests can still override via Parameters__sql-password / Parameters__postgres-password.
var defaultSqlPassword = LocalSqlSettings.SharedSaPassword;
// Pinned, not "2025-latest": every other image here is pinned, and a floating tag moving mid-CI-run was the
// suspected trigger for the 2026-09-08 Aspire Mesh Tests failure (audit tests hit SqlException pre-login
// handshake errors against a freshly pulled build). Bump deliberately when a newer CU is needed.
var sqlServerImageTag = "2025-CU8-ubuntu-22.04";

// D-035: the hosting lane is a preset, not a switch. It seeds the DEFAULT of every provider switch below;
// each switch's own env var or config key still wins. Unset means Azure, which is byte-for-byte today's graph.
var lane = LaneDefaults.Resolve(builder.Configuration);
var portableLane = lane.IsPortable;

// D-020: exactly one relational server runs locally, chosen by TASKFLOW_DB_PROVIDER / Database:Provider
// (default SqlServer; PostgreSql in the Portable lane). Every host receives the same choice as
// Database__Provider so UseTaskFlowProvider agrees.
var dbProviderName = lane.Database;
var usePostgres = string.Equals(dbProviderName, "PostgreSql", StringComparison.OrdinalIgnoreCase);

// D-034: exactly one broker runs locally, chosen by TASKFLOW_MESSAGING_PROVIDER / Messaging:Provider
// (default ServiceBus; RabbitMq in the Portable lane). Every host receives the same choice as Messaging__Provider.
var messagingProviderName = lane.Messaging;
var useRabbitMq = string.Equals(messagingProviderName, "RabbitMq", StringComparison.OrdinalIgnoreCase);

// D-040: the pgvector embedding path is opt-in (Search:Provider / TASKFLOW_SEARCH_PROVIDER; neither lane
// defaults to it, because it needs an embedding endpoint). Read from the same resolved lane switches the
// hosts receive as Search__Provider, so the subscription, the queue and the Functions trigger declared here
// cannot disagree with what the hosts resolve.
var usePgVector = string.Equals(lane.Search, "PgVector", StringComparison.OrdinalIgnoreCase);

// Infrastructure resources
// In Testing mode: non-persistent, no named volume, random port - ensures fresh container with known password.
// In dev/prod: persistent with named volume on fixed port.
IResourceBuilder<IResourceWithConnectionString> taskflowDb;
IResourceBuilder<IResource> dbServer;
if (usePostgres)
{
    var postgresPassword = builder.AddParameter("postgres-password", defaultSqlPassword, secret: true);
    var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: isTesting ? null : 35432)
        .WithImage("pgvector/pgvector")
        .WithImageTag("pg17");
    if (!isTesting)
        postgres = postgres.WithLifetime(ContainerLifetime.Persistent)
                           .WithDataVolume("taskflow-postgres-data");
    taskflowDb = postgres.AddDatabase("taskflowdb");
    dbServer = postgres;
}
else
{
    var sqlPassword = builder.AddParameter("sql-password", defaultSqlPassword, secret: true);
    var sql = builder.AddSqlServer("sql", sqlPassword, port: isTesting ? null : 38433)
        .WithImageTag(sqlServerImageTag);
    if (!isTesting)
        sql = sql.WithLifetime(ContainerLifetime.Persistent)
                 .WithDataVolume("taskflow-sql-data");
    taskflowDb = sql.AddDatabase("taskflowdb");
    dbServer = sql;
}

var redis = builder.AddRedis("redis")
    .WithImageTag("latest");
if (!isTesting)
    redis = redis.WithLifetime(ContainerLifetime.Persistent)
                 .WithDataVolume("taskflow-redis-data");

// Object storage and the Table audit sink.
// Azure lane: the Azure Storage emulator (Azurite) supplies both.
// Portable lane (D-037): MinIO supplies the S3 arm and the audit sink is relational, so neither the
// emulator nor the Functions host it also backs is declared at all.
// Not using ContainerLifetime.Persistent for Azurite - persistent emulator containers survive Aspire
// restarts but get stranded on deleted Podman networks, causing netavark "eth2 already exists" errors.
IResourceBuilder<AzureStorageResource>? storage = null;
IResourceBuilder<AzureBlobStorageResource>? blobs = null;
IResourceBuilder<AzureTableStorageResource>? tables = null;
IResourceBuilder<ContainerResource>? minio = null;
IResourceBuilder<ParameterResource>? minioAccessKey = null;
IResourceBuilder<ParameterResource>? minioSecretKey = null;

if (portableLane)
{
    // Dev-only credentials for a local container, exposed as parameters so a run can override them with
    // Parameters__minio-access-key / Parameters__minio-secret-key. They are never real secrets, and the
    // bucket itself is created by the Api/Scheduler startup task (IS3BucketProvisioner), not an init container.
    minioAccessKey = builder.AddParameter("minio-access-key", "taskflowminio");
    minioSecretKey = builder.AddParameter("minio-secret-key", "taskflowminio-dev-secret", secret: true);

    minio = builder.AddContainer("minio", "minio/minio")
        .WithImageTag("RELEASE.2025-09-07T16-13-09Z")
        .WithArgs("server", "/data", "--console-address", ":9001")
        .WithEnvironment("MINIO_ROOT_USER", minioAccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", minioSecretKey)
        .WithHttpEndpoint(targetPort: 9000, name: "s3")
        .WithHttpEndpoint(targetPort: 9001, name: "console");

    if (!isTesting)
        minio = minio.WithLifetime(ContainerLifetime.Persistent)
                     .WithVolume("taskflow-minio-data", "/data");
}
else
{
    storage = builder.AddAzureStorage("AzureStorage")
        .RunAsEmulator(emulator => emulator.WithImageTag("latest"));
    blobs = storage.AddBlobs("BlobStorage1");
    tables = storage.AddTables("TableStorage1");
}

// Broker: exactly one of the two is declared. The Service Bus emulator brings its own SQL Server sidecar, so
// nothing about it is free; declaring it under RabbitMq would burn a container the run never touches.
IResourceBuilder<AzureServiceBusResource>? serviceBus = null;
IResourceBuilder<RabbitMQServerResource>? rabbitMq = null;

if (useRabbitMq)
{
    // Single node with the management plugin: enough for the dev/staging proof. Production wants a managed
    // broker or a cluster (see infra/README.md).
    rabbitMq = builder.AddRabbitMQ("rabbitmq").WithManagementPlugin();
    if (!isTesting)
        rabbitMq = rabbitMq.WithLifetime(ContainerLifetime.Persistent)
                           .WithDataVolume("taskflow-rabbitmq-data");
}
else
{
    // Azure Service Bus - emulator
    var sb = builder.AddAzureServiceBus("ServiceBus1")
        .RunAsEmulator(emulator => emulator.WithImageTag("latest"));
    var domainEventsTopic = sb.AddServiceBusTopic("DomainEvents");

    // One subscription per consumer, each filtered on the EventType application property the envelope sets, so a
    // slow AI review cannot delay projection and each consumer owns its own delivery count and dead-letter queue.
    // A correlation filter matches one value, so the projection subscription needs one rule per event type.
    // The emulator implements no duplicate detection (the deployed namespace does, see service-bus.bicep), so the
    // D-029 ConsumerInbox is what proves replay safety locally - and it is the only dedup on RabbitMQ (D-034).
    AddEventTypeSubscription(domainEventsTopic, "projection",
        ["TaskItemCreatedEvent", "TaskItemStatusChangedEvent", "TaskItemCompletedEvent"]);
    AddEventTypeSubscription(domainEventsTopic, "ai-review", ["TaskItemCreatedEvent"]);
    AddEventTypeSubscription(domainEventsTopic, "workflow", ["TaskItemCreatedEvent"]);
    // D-040: declared only on the PgVector arm - a subscription nothing drains just fills up.
    if (usePgVector)
        AddEventTypeSubscription(domainEventsTopic, "embedding",
            ["TaskItemCreatedEvent", "TaskItemContentChangedEvent"]);

    sb.AddServiceBusQueue("TaskCommands");

    // The Service Bus emulator bundles its own SQL Server sidecar (ServiceBus1-mssql); the Aspire package
    // hardcodes that image, and RunAsEmulator's callback cannot reach it. Override it here so it matches
    // the `sql` container tag and lets Docker share layers instead of pulling a second SQL Server major version.
    builder.CreateResourceBuilder(
            (ContainerResource)builder.Resources.Single(r => r.Name == "ServiceBus1-mssql"))
        .WithImageTag(sqlServerImageTag);

    serviceBus = sb;
}

static void AddEventTypeSubscription(
    IResourceBuilder<AzureServiceBusTopicResource> topic, string name, string[] eventTypes)
{
    topic.AddServiceBusSubscription(name)
        .WithProperties(subscription =>
        {
            subscription.MaxDeliveryCount = 5;
            subscription.LockDuration = TimeSpan.FromMinutes(5);
            subscription.DeadLetteringOnMessageExpiration = true;
            foreach (var eventType in eventTypes)
            {
                subscription.Rules.Add(new AzureServiceBusRule($"EventType-{eventType}")
                {
                    CorrelationFilter = new AzureServiceBusCorrelationFilter
                    {
                        Properties = { ["EventType"] = eventType }
                    }
                });
            }
        });
}

// Azure Cosmos DB - emulator (see AzureStorage comment re: Persistent lifetime)
// Skipped in Testing: the emulator is heavy (~1.3 GB) and not needed for audit pipeline tests.
// Skipped in the Portable lane: the read model is relational there (D-038).
// The API's AddCosmosDbServices falls back to NoOpTaskViewRepository when the connection string is absent.
if (!isTesting && !portableLane)
{
    builder.AddAzureCosmosDB("CosmosDb1")
        .RunAsEmulator();
}

// AI: Azure AI Foundry. Two independent axes - lifecycle x consumption.
//
// Axis 1 - lifecycle (where the Foundry resource comes from):
//  - Foundry Local       -> API host bootstraps Microsoft.AI.Foundry.Local directly.
//  - Provision new       -> AddFoundry(...).AddDeployment(...), Bicep creates account + model on publish
//                           (and in run mode when Azure provisioning secrets are set).
//  - Connect to existing -> RunAsExisting/PublishAsExisting against an already-provisioned account
//                           (see the commented block below). Deployment name must already exist there.
//  - Disabled            -> no "chat" resource; the API registers a no-op IChatClient and still boots.
//
// Axis 2 - consumption: this app consumes raw model inference (IChatClient over the "chat" deployment;
// the resource name is the connection name consumers bind to, CHAT_ENDPOINT/etc.). Foundry projects +
// server-hosted agents (AddProject/AddPromptAgent, or pre-existing agents via the client SDK) are an
// Azure-only escalation - see the commented "Foundry project + prompt agent" block after the API host
// and README "AI Demos" -> "Projects and agents". They are documented but not wired by default.
//
// Test mode forces no-op for local AI unless TASKFLOW_ASPIRE_ENABLE_FOUNDRY_LOCAL=true;
// Azure Foundry can still be explicitly configured.
// The Portable lane never provisions Foundry: its AI arm is an OpenAI-compatible endpoint reached over
// plain configuration (D-041), so there is no Azure resource for this graph to declare.
IResourceBuilder<FoundryDeploymentResource>? chat = null;
var azureFoundryConfigured = !portableLane
    && (builder.ExecutionContext.IsPublishMode
        || !string.IsNullOrWhiteSpace(builder.Configuration["AiServices:FoundryEndpoint"])
        || Environment.GetEnvironmentVariable("TASKFLOW_USE_AZURE_FOUNDRY") == "true");

if (azureFoundryConfigured)
{
    // Provisions an Azure AI Foundry account + deployment on publish; connects to it in run mode
    // when Azure provisioning is configured (azd / user secrets).
    var foundry = builder.AddFoundry("foundry");
    chat = foundry.AddDeployment("chat", FoundryModel.OpenAI.Gpt4oMini);

    // OPT-IN: connect to an EXISTING Azure Foundry account instead of provisioning a new one.
    // The "chat" deployment must already exist in that account. RunAsExisting binds in run mode;
    // PublishAsExisting binds the published graph. Parameters resolve from config/user-secrets
    // (Parameters:foundry-name / Parameters:foundry-rg). Uncomment and set AiServices:FoundryResourceName
    // + AiServices:FoundryResourceGroup to use it.
    // var foundryName = builder.AddParameter("foundry-name");
    // var foundryRg = builder.AddParameter("foundry-rg");
    // chat = builder.AddFoundry("foundry").RunAsExisting(foundryName, foundryRg)
    //     .AddDeployment("chat", FoundryModel.OpenAI.Gpt4oMini);
}

// D-023 column encryption keys for every host that maps TaskItem. Generated once and persisted to user secrets
// (Base64KeyParameterDefault) so the persistent local database stays decryptable across restarts; tests get a
// fresh key per run. Override with Parameters__column-encryption-key / Parameters__blind-index-key.
var columnEncryptionKey = builder.AddParameter("column-encryption-key", new Base64KeyParameterDefault(), secret: true, persist: !isTesting);
var blindIndexKey = builder.AddParameter("blind-index-key", new Base64KeyParameterDefault(), secret: true, persist: !isTesting);

// Single migration owner. Runtime hosts wait for this project and never mutate schema on startup.
// Connection names stay separate even when local Aspire maps them to the same taskflowdb database.
var migrator = builder.AddProject<Projects.TaskFlow_DatabaseMigrator>("taskflowmigrator")
    .WithReference(taskflowDb, connectionName: "TaskFlowDbContextTrxn")
    .WithReference(taskflowDb, connectionName: "TaskFlowFlowEngineDbContext")
    .WithReference(taskflowDb, connectionName: "TickerQDbContext")
    .WithEnvironment("Database__Provider", dbProviderName)
    .WithEnvironment("Database__Encryption__LocalKeyBase64", columnEncryptionKey)
    .WithEnvironment("Database__Encryption__BlindIndexKeyBase64", blindIndexKey)
    .WaitFor(dbServer);
migrator = WithLaneEnvironment(migrator);

// API host.
//
// D-054: the Api listens on two cleartext ports, declared in its own appsettings Kestrel:Endpoints
// ("Http" 8080 REST, "Grpc" 8081 HTTP/2). Aspire turns each Kestrel section key into an endpoint of
// that name - two http-scheme entries, so the endpoint names are the keys rather than the scheme -
// carries Protocols: Http2 across as transport "http2", and injects Kestrel__Endpoints__<name>__Url
// with the port it allocated. There is deliberately no WithHttpEndpoint(targetPort: 8081) here: the
// endpoint already exists, and re-declaring the target port would pin the DCP proxy port and the app
// port to the same 8081 in run mode. Endpoint names compare case-insensitively, so GetEndpoint("http")
// below still resolves the "Http" endpoint.
var api = builder.AddProject<Projects.TaskFlow_Api>("taskflowapi")
    .WithReference(taskflowDb, connectionName: "TaskFlowDbContextTrxn")
    .WithReference(taskflowDb, connectionName: "TaskFlowDbContextQuery")
    .WithReference(taskflowDb, connectionName: "TaskFlowFlowEngineDbContext")
    .WithReference(redis, connectionName: "Redis1")
    .WithEnvironment("Database__Provider", dbProviderName)
    .WithEnvironment("Messaging__Provider", messagingProviderName)
    .WithEnvironment("Database__Encryption__LocalKeyBase64", columnEncryptionKey)
    .WithEnvironment("Database__Encryption__BlindIndexKeyBase64", blindIndexKey)
    .WaitForCompletion(migrator)
    .WaitFor(dbServer)
    .WaitFor(redis);
api = WithAuditSink(api);
api = WithObjectStorage(api);
api = WithBroker(api);
api = WithLaneEnvironment(api);

// Wire the Azure Foundry chat model into the API when a deployment was created. Local mode wires no
// chat resource; the bootstrapper owns the temporary SDK-direct Foundry Local fallback.
if (chat is not null)
{
    api = api.WithReference(chat);
}

// OPT-IN (Azure-only): Foundry project + server-hosted prompt agent.
// A project is the container for server-hosted agents, deployments, and tool connections. A prompt
// agent is a declarative agent (model + instructions + tools). Prompt agents ALWAYS deploy to Azure
// Foundry, even under `aspire run` - there is no offline path - so this stays commented by default.
// Referencing the project injects PROJ_URI (the project endpoint) into the API; consume pre-existing
// agents at runtime with AIProjectClient.AsAIAgent(...) in a bootstrapper-owned provider extension.
//
// var foundry = builder.AddFoundry("foundry");
// var project = foundry.AddProject("taskflow-project");
// var projectChat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);
// var codeInterp = project.AddCodeInterpreterTool("code-interp");
// var webSearch = project.AddWebSearchTool("web-search");
// var assistant = project.AddPromptAgent(projectChat, "task-assistant",
//         instructions: "You are an assistant for TaskFlow.")
//     .WithTool(codeInterp)
//     .WithTool(webSearch);
// api = api.WithReference(project);   // or .WithReference(assistant)

if (isTesting)
{
    api = api.WithEnvironment("Cors__AllowedOrigins__0", "http://localhost");

    if (foundryLocalAvailableInTesting)
    {
        // This is deliberately API-only. Functions stay local-AI disabled in test mode so
        // the optional lane does not start multiple Foundry Local hosts in the same graph.
        // RequireFoundryLocal prevents an opt-in smoke from silently falling back to no-op.
        api = api
            .WithEnvironment("AiServices__DisableFoundryLocal", "false")
            .WithEnvironment("AiServices__RequireFoundryLocal", "true");

        // Let local smoke runs pin the model or HTTP endpoint without changing appsettings.
        var foundryLocalModel = Environment.GetEnvironmentVariable("TASKFLOW_FOUNDRY_LOCAL_MODEL");
        if (!string.IsNullOrWhiteSpace(foundryLocalModel))
            api = api.WithEnvironment("AiServices__LocalModel", foundryLocalModel);

        var foundryLocalWebUrl = Environment.GetEnvironmentVariable("TASKFLOW_FOUNDRY_LOCAL_WEB_URL");
        if (!string.IsNullOrWhiteSpace(foundryLocalWebUrl))
            api = api.WithEnvironment("AiServices__LocalWebUrl", foundryLocalWebUrl);
    }
    else
    {
        api = api.WithEnvironment("AiServices__DisableFoundryLocal", "true");
    }
}

if (!string.IsNullOrWhiteSpace(applicationStyle))
{
    api.WithEnvironment("TASKFLOW_APPLICATION_STYLE", applicationStyle);
}

// Gateway is part of the default test graph because all browser-facing hosts route through it.
var gateway = builder.AddProject<Projects.TaskFlow_Gateway>("taskflowgateway")
    .WithReference(api)
    .WithEnvironment("ReverseProxy__Routes__api-route__Match__Path", "/api/{**catch-all}")
    .WithEnvironment("ReverseProxy__Clusters__api-cluster__Destinations__api__Address", api.GetEndpoint("http"))
    .WaitFor(api);
gateway = WithLaneEnvironment(gateway);

var blazor = builder.AddProject<Projects.TaskFlow_Blazor>("taskflowblazor")
    .WithReference(gateway)
    .WithEnvironment("Gateway__BaseUrl", gateway.GetEndpoint("http"))
    // D-054: the one in-cluster service-to-service hop. Blazor Server reads the dashboard summary and
    // the picker metadata straight from the Api's gRPC listener; every public client still goes through
    // the gateway over REST. WithReference records the dependency and publishes
    // services__taskflowapi__Grpc__0; the explicit variable is what the host actually reads, and it is
    // the same one Bicep and the compose lane set, so all three lanes configure this identically.
    .WithReference(api.GetEndpoint("Grpc"))
    .WithEnvironment("Grpc__TaskFlowRead__Address", api.GetEndpoint("Grpc"))
    .WaitFor(gateway)
    .WithExternalHttpEndpoints();
blazor = WithLaneEnvironment(blazor);

if (!isTesting || schedulerAvailableInTesting)
{
    // Scheduler host. Opt-in under test: without it nothing staged is ever delivered, so the messaging mesh
    // test asks for it explicitly rather than making every Aspire class pay for the extra boot.
    var scheduler = builder.AddProject<Projects.TaskFlow_Scheduler>("taskflowscheduler")
        .WithReference(taskflowDb, connectionName: "TaskFlowDbContextTrxn")
        .WithReference(taskflowDb, connectionName: "TaskFlowDbContextQuery")
        .WithReference(taskflowDb, connectionName: "TaskFlowFlowEngineDbContext")
        .WithReference(taskflowDb, connectionName: "TickerQDbContext")
        .WithReference(redis, connectionName: "Redis1")
        .WithEnvironment("Database__Provider", dbProviderName)
        .WithEnvironment("Messaging__Provider", messagingProviderName)
        .WithEnvironment("Database__Encryption__LocalKeyBase64", columnEncryptionKey)
        .WithEnvironment("Database__Encryption__BlindIndexKeyBase64", blindIndexKey)
        // Two replicas so the outbox/blob lease path is exercised locally (D-026): both drain, neither doubles
        // up. One replica under test so the graph boot stays inside the mesh startup budget.
        .WithReplicas(isTesting ? 1 : 2)
        .WaitForCompletion(migrator)
        .WaitFor(dbServer);
    scheduler = WithAuditSink(scheduler);
    // Portable lane only. The Scheduler runs the same S3 bucket provisioning startup task and the blob delete
    // worker, so it needs the MinIO settings; the Azure lane keeps its existing wiring, where the Scheduler
    // deliberately holds no blob reference, so this stays a lane addition rather than a change to today's graph.
    if (minio is not null)
        scheduler = WithObjectStorage(scheduler);
    scheduler = WithBroker(scheduler);
    scheduler = WithLaneEnvironment(scheduler);

    if (!string.IsNullOrWhiteSpace(applicationStyle))
    {
        scheduler.WithEnvironment("TASKFLOW_APPLICATION_STYLE", applicationStyle);
    }
}

if (!isTesting || reactAvailableInTesting)
{
    builder.AddViteApp("taskflowreact", "../../../UI/TaskFlow.React")
        .WithReference(gateway)
        .WithEnvironment("VITE_API_BASE_URL", gateway.GetEndpoint("http"))
        .WaitFor(gateway)
        .WithExternalHttpEndpoints();
}

if (!isTesting || unoWasmAvailableInTesting)
{
    var unoWasm = builder.AddProject<Projects.TaskFlow_Uno_WasmHost>("taskflowuno")
        .WithReference(gateway)
        .WaitFor(gateway)
        .WithExternalHttpEndpoints();

    var publishedDistPath = Environment.GetEnvironmentVariable("TASKFLOW_UNO_WASM_DIST_PATH");
    if (isTesting && !string.IsNullOrWhiteSpace(publishedDistPath))
    {
        unoWasm.WithEnvironment("UnoWasm__DistPath", publishedDistPath);
    }
}

// The Functions host is Azure-only (it needs the storage account for its own host state), and its consumer
// logic is already shared with the Scheduler's RabbitMQ handlers - so the Portable lane simply does not
// declare it rather than declaring a host that cannot run there (D-036).
if (!portableLane && (!isTesting || functionsAvailableInTesting))
{
    // Functions host
    var functions = builder.AddAzureFunctionsProject<Projects.TaskFlow_Functions>("taskflowfunctions")
        .WithHostStorage(storage!)
        // The Functions host process emits request telemetry itself; suppress the worker's ASP.NET Core
        // instrumentation so requests are not double-reported when the Azure Monitor distro is active.
        .WithEnvironment("TASKFLOW_SUPPRESS_ASPNETCORE_INSTRUMENTATION", "true")
        .WithReference(taskflowDb, connectionName: "TaskFlowDbContextTrxn")
        .WithReference(taskflowDb, connectionName: "TaskFlowDbContextQuery")
        .WithReference(taskflowDb, connectionName: "TaskFlowFlowEngineDbContext")
        .WithReference(tables!)
        .WithReference(blobs!)
        .WithEnvironment("Database__Provider", dbProviderName)
        .WithEnvironment("Messaging__Provider", messagingProviderName)
        .WithEnvironment("Database__Encryption__LocalKeyBase64", columnEncryptionKey)
        .WithEnvironment("Database__Encryption__BlindIndexKeyBase64", blindIndexKey)
        .WaitForCompletion(migrator)
        .WaitFor(dbServer)
        .WaitFor(storage!);
    functions = WithBroker(functions);
    functions = WithLaneEnvironment(functions);

    if (useRabbitMq)
    {
        // D-034: the Service Bus triggers stay compiled in but inert; the Scheduler consumes from RabbitMQ.
        // Disabling by name beats deleting them, so one deployment can flip providers.
        functions = functions
            .WithEnvironment("AzureWebJobs.ProcessTaskProjection.Disabled", "true")
            .WithEnvironment("AzureWebJobs.ProcessTaskAiReview.Disabled", "true")
            .WithEnvironment("AzureWebJobs.ProcessTaskWorkflowStart.Disabled", "true");
    }

    // D-040: same mechanism, second reason. The embedding trigger also has to be off whenever the embedding
    // subscription was not created, or the Functions host fails at startup binding a listener to a
    // subscription that does not exist.
    if (useRabbitMq || !usePgVector)
        functions = functions.WithEnvironment("AzureWebJobs.ProcessTaskEmbedding.Disabled", "true");

    if (!string.IsNullOrWhiteSpace(applicationStyle))
    {
        functions.WithEnvironment("TASKFLOW_APPLICATION_STYLE", applicationStyle);
    }

    // Wire the Azure Foundry chat model into Functions for the event-driven AI readiness review (D6).
    // Without this reference, Functions follows the same bootstrapper-owned local/no-op fallback as API.
    if (chat is not null)
    {
        functions.WithReference(chat);
    }

    if (isTesting)
    {
        functions.WithEnvironment("AiServices__DisableFoundryLocal", "true");
    }
}

// One place decides how a host reaches the broker, so adding a host cannot forget the reference or the wait.
IResourceBuilder<T> WithBroker<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment, IResourceWithWaitSupport
{
    if (rabbitMq is not null)
        return host.WithReference(rabbitMq, connectionName: "RabbitMq1").WaitFor(rabbitMq);

    return host.WithReference(serviceBus!).WaitFor(serviceBus!);
}

// Same rule for object storage (D-037): one place decides whether a host talks to the Azurite blob emulator
// or to MinIO, so adding a host cannot forget the reference. SigV4 signs the Host header into a presigned
// URL, so ServiceUrl and PublicServiceUrl are the same allocated endpoint here - locally the app and the
// browser reach MinIO through the same host-mapped address.
IResourceBuilder<T> WithObjectStorage<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment, IResourceWithWaitSupport
{
    if (minio is null)
        return host.WithReference(blobs!);

    return host
        .WithEnvironment("Storage__S3__ServiceUrl", minio.GetEndpoint("s3"))
        .WithEnvironment("Storage__S3__PublicServiceUrl", minio.GetEndpoint("s3"))
        .WithEnvironment("Storage__S3__AccessKeyId", minioAccessKey!)
        .WithEnvironment("Storage__S3__SecretAccessKey", minioSecretKey!)
        .WithEnvironment("Storage__S3__ForcePathStyle", "true")
        .WaitFor(minio);
}

// D-035: every host learns the lane and the switch values this graph actually declared containers for, the
// same way Bicep and the compose lane set them. In the Azure lane this is only Hosting__Lane=Azure - the
// remaining switches keep their own runtime-derived defaults, which is what "Azure means today's behavior"
// has to mean for the AI, Search and DataProtection switches.
IResourceBuilder<T> WithLaneEnvironment<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment
{
    foreach (var (key, value) in lane.HostEnvironment)
    {
        host = host.WithEnvironment(key, value);
    }

    return host;
}

// The Azure Table audit sink only exists in the Azure lane; the Portable lane audits relationally (D-039).
IResourceBuilder<T> WithAuditSink<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment =>
    tables is null ? host : host.WithReference(tables);

await builder.Build().RunAsync();

// Required so Aspire test hosts (AspireTestHost, PlaywrightAspireHost) can resolve this AppHost via
// Type.GetType("Program, AppHost") for DistributedApplicationTestingBuilder. The .NET 10 auto-generated
// Program is internal, so the explicit public declaration is load-bearing here despite ASP0027.
#pragma warning disable ASP0027 // Public partial Program is required for cross-assembly reflection lookup.
/// <summary>Configures program host behavior for TaskFlow runtime services.</summary>
public partial class Program;
#pragma warning restore ASP0027
