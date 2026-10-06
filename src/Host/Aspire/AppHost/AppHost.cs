using AppHost;
using Aspire.Hosting.Azure;
using System.Data.Common;
using TaskFlow.Hosting;

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
var fullLaneAvailableInTesting =
    Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_FULL_LANE") == "true";
// The Scheduler owns the outbox dispatcher, so the messaging mesh test needs it in the graph. It is opt-in
// under test for the same reason Functions and the SPAs are: one more host to boot inside the startup budget.
var schedulerAvailableInTesting =
    Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE") == "true";
// Keep the database password stable across restarts so persistent volumes remain usable. Tests can still
// override via Parameters__sql-password / Parameters__postgres-password.
var defaultSqlPassword = LocalSqlSettings.SharedSaPassword;

// D-060: the strict lane resolver owns every core provider. Unset means NonAzure (TASKFLOW_LANE=Azure opts into
// the Azure topology); Portable remains a deprecated input alias that normalizes to NonAzure before this
// graph is built.
var lane = LaneDefaults.Resolve(builder.Configuration);
var nonAzureLane = lane.Lane == HostingLane.NonAzure;

// D-020/D-060: exactly one relational server runs locally. Azure owns SQL Server; NonAzure owns PostgreSQL.
var dbProviderName = lane.Database;
var usePostgres = string.Equals(dbProviderName, "PostgreSql", StringComparison.OrdinalIgnoreCase);

// D-034/D-060: exactly one broker runs locally. Azure owns Service Bus; NonAzure owns RabbitMQ.
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
// Persistent containers are proxyless: the container runtime publishes each port on 127.0.0.1 only. Their
// endpoints therefore target 127.0.0.1 (UseIpv4Loopback), so connection strings, service URLs and Aspire's own
// health checks name that address. Left at "localhost", .NET clients try ::1 first, and under Podman with WSL
// mirrored networking a ::1 connect to such a port hangs instead of being refused, so no persistent resource
// ever turned healthy.
IResourceBuilder<IResourceWithConnectionString> taskflowDb;
if (usePostgres)
{
    var postgresPassword = builder.AddParameter("postgres-password", defaultSqlPassword, secret: true);
    var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: isTesting ? null : 35432)
        .WithImage(ContainerImages.PostgreSqlRepository)
        .WithImageTag(ContainerImages.PostgreSqlTag)
        .WithEnvironment("POSTGRES_DB", "taskflowdb");
    // PostgreSQL 18 images keep data under a major-version directory below /var/lib/postgresql and refuse to
    // start when /var/lib/postgresql/data is a mount, which is where WithDataVolume puts it. Mount the parent
    // instead, as deploy/compose does.
    if (!isTesting)
        postgres = UseIpv4Loopback(postgres.WithLifetime(ContainerLifetime.Persistent)
                           .WithVolume("taskflow-postgres-data", "/var/lib/postgresql"));
    taskflowDb = postgres.AddDatabase("taskflowdb");
}
else
{
    var sqlPassword = builder.AddParameter("sql-password", defaultSqlPassword, secret: true);
    var sql = builder.AddSqlServer("sql", sqlPassword, port: isTesting ? null : 38433)
        .WithImage(ContainerImages.SqlServerRepository)
        .WithImageRegistry(ContainerImages.MicrosoftContainerRegistry)
        .WithImageTag(ContainerImages.SqlServerTag);
    if (!isTesting)
        sql = UseIpv4Loopback(sql.WithLifetime(ContainerLifetime.Persistent)
                 .WithDataVolume("taskflow-sql-data"));
    taskflowDb = sql.AddDatabase("taskflowdb");
}

var redis = builder.AddRedis("redis")
    .WithImage(ContainerImages.RedisRepository)
    .WithImageTag(ContainerImages.RedisTag);
if (!isTesting)
    redis = UseIpv4Loopback(redis.WithLifetime(ContainerLifetime.Persistent)
                 .WithDataVolume("taskflow-redis-data"));

// Object storage and the Table audit sink.
// Azure lane: the Azure Storage emulator (Azurite) supplies both.
// NonAzure lane (D-037): SeaweedFS supplies the S3 arm and the audit sink is relational, so neither the
// emulator nor the Functions host it also backs is declared at all.
// Not using ContainerLifetime.Persistent for Azurite - persistent emulator containers survive Aspire
// restarts but get stranded on deleted Podman networks, causing netavark "eth2 already exists" errors.
IResourceBuilder<AzureStorageResource>? storage = null;
IResourceBuilder<AzureBlobStorageResource>? blobs = null;
IResourceBuilder<AzureTableStorageResource>? tables = null;
IResourceBuilder<ContainerResource>? seaweedFs = null;
IResourceBuilder<ParameterResource>? s3AccessKey = null;
IResourceBuilder<ParameterResource>? s3SecretKey = null;

if (nonAzureLane)
{
    // Dev-only credentials for a local container, exposed as parameters so a run can override them with
    // Parameters__s3-access-key / Parameters__s3-secret-key. They are never real secrets, and the
    // bucket itself is created by the Api/Scheduler startup task (IS3BucketProvisioner), not an init container.
    s3AccessKey = builder.AddParameter("s3-access-key", "taskflow-development");
    s3SecretKey = builder.AddParameter("s3-secret-key", "taskflow-development-secret", secret: true);

    seaweedFs = builder.AddContainer("seaweedfs", ContainerImages.SeaweedFsRepository)
        .WithImageTag(ContainerImages.SeaweedFsTag)
        .WithArgs("mini", "-dir=/data")
        .WithEnvironment("AWS_ACCESS_KEY_ID", s3AccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", s3SecretKey)
        .WithHttpEndpoint(targetPort: 8333, name: "s3")
        .WithHttpEndpoint(targetPort: 9333, name: "master")
        .WithHttpHealthCheck(path: "/cluster/status", endpointName: "master");

    if (!isTesting)
        seaweedFs = UseIpv4Loopback(seaweedFs.WithLifetime(ContainerLifetime.Persistent)
                             .WithVolume("taskflow-seaweedfs-data", "/data"));
}
else
{
    storage = builder.AddAzureStorage("AzureStorage")
        .RunAsEmulator(emulator => emulator
            .WithImage(ContainerImages.AzuriteRepository)
            .WithImageRegistry(ContainerImages.MicrosoftContainerRegistry)
            .WithImageTag(ContainerImages.AzuriteTag));
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
    rabbitMq = builder.AddRabbitMQ("rabbitmq")
        .WithManagementPlugin()
        .WithImage(ContainerImages.RabbitMqRepository)
        .WithImageTag(ContainerImages.RabbitMqTag);
    if (!isTesting)
        rabbitMq = UseIpv4Loopback(rabbitMq.WithLifetime(ContainerLifetime.Persistent)
                           .WithDataVolume("taskflow-rabbitmq-data"));
}
else
{
    // Azure Service Bus - emulator
    var sb = builder.AddAzureServiceBus("ServiceBus1")
        .RunAsEmulator(emulator => emulator
            .WithImage(ContainerImages.ServiceBusEmulatorRepository)
            .WithImageRegistry(ContainerImages.MicrosoftContainerRegistry)
            .WithImageTag(ContainerImages.ServiceBusEmulatorTag));
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
    // hardcodes that image, and RunAsEmulator's callback cannot reach it. D-060 intentionally keeps the
    // sidecar on SQL Server 2022 while the application database runs SQL Server 2025.
    var serviceBusSqlSidecar = builder.Resources
        .OfType<ContainerResource>()
        .SingleOrDefault(r => r.Name == "ServiceBus1-mssql");
    if (serviceBusSqlSidecar is not null)
    {
        builder.CreateResourceBuilder(serviceBusSqlSidecar)
            .WithImage(ContainerImages.ServiceBusSqlServerRepository)
            .WithImageRegistry(ContainerImages.MicrosoftContainerRegistry)
            .WithImageTag(ContainerImages.ServiceBusSqlServerTag);
    }

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
// Skipped in the NonAzure lane: the read model is PostgreSQL JSONB or explicitly MongoDB (D-038).
// Strict Azure startup requires Cosmos configuration when this resource is selected.
IResourceBuilder<AzureCosmosDBResource>? cosmos = null;
if (!nonAzureLane && (!isTesting || fullLaneAvailableInTesting))
{
    cosmos = builder.AddAzureCosmosDB("CosmosDb1")
        .RunAsEmulator(emulator => emulator
            .WithImage(ContainerImages.CosmosEmulatorRepository)
            .WithImageRegistry(ContainerImages.MicrosoftContainerRegistry)
            .WithImageTag(ContainerImages.CosmosEmulatorTag));
}

// PostgreSQL JSONB is the NonAzure default, so MongoDB is absent unless the read-model switch explicitly
// selects it. A mongodb-scheme endpoint gives the driver the connection-string form it expects.
IResourceBuilder<ContainerResource>? mongoDb = null;
if (nonAzureLane && string.Equals(lane.ReadModel, "MongoDb", StringComparison.OrdinalIgnoreCase))
{
    mongoDb = builder.AddContainer("mongodb", ContainerImages.MongoDbRepository)
        .WithImageTag(ContainerImages.MongoDbTag)
        .WithEndpoint(targetPort: 27017, name: "mongodb", scheme: "mongodb");
    if (!isTesting)
        mongoDb = UseIpv4Loopback(mongoDb.WithLifetime(ContainerLifetime.Persistent)
                         .WithVolume("taskflow-mongodb-data", "/data/db"));
}

// Azure AI Foundry provisioning is external because its former hosting package also installed a native
// local-model runtime. Accept either a complete ConnectionStrings:chat value or build a keyless connection
// from an HTTPS endpoint plus deployment. Aspire.Azure.AI.Inference uses DefaultAzureCredential when Key is absent.
// The resolved AI provider is the only switch (D-041/D-060): the hosts receive it as AiServices__Provider through
// WithLaneEnvironment and register the Foundry client only for AzureInference, so the graph wires the chat
// connection only then too. Foundry settings under any other provider would be wired into hosts that ignore them.
var foundryEndpoint = builder.Configuration["AiServices:FoundryEndpoint"];
var foundryDeployment = builder.Configuration["AiServices:AgentModelDeployment"];
var chatConnectionString = builder.Configuration["ConnectionStrings:chat"];
var azureFoundryRequested = string.Equals(lane.AiServices, "AzureInference", StringComparison.Ordinal);
if (!azureFoundryRequested)
{
    var strayFoundrySettings = new (string Key, string? Value)[]
        {
            ("ConnectionStrings:chat", chatConnectionString),
            ("AiServices:FoundryEndpoint", foundryEndpoint),
            ("AiServices:AgentModelDeployment", foundryDeployment)
        }
        .Where(setting => !string.IsNullOrWhiteSpace(setting.Value))
        .Select(setting => setting.Key)
        .ToArray();
    if (strayFoundrySettings.Length > 0)
    {
        throw new InvalidOperationException(
            $"Azure AI Foundry settings ({string.Join(", ", strayFoundrySettings)}) are configured, but the resolved " +
            $"AI provider ({HostingLaneResolver.AiConfigurationKey} / {HostingLaneResolver.AiEnvironmentVariable}) is " +
            $"'{lane.AiServices}' on the {lane.Lane} lane, so no host would use them. Select AzureInference on the " +
            "Azure lane to use Azure AI Foundry, or remove the Foundry settings.");
    }
}

IResourceBuilder<IResourceWithConnectionString>? chat = null;
string? keylessChatConnectionString = null;
if (azureFoundryRequested)
{
    if (!string.IsNullOrWhiteSpace(chatConnectionString))
    {
        ValidateAzureChatConnectionString(chatConnectionString);
        chat = builder.AddConnectionString("chat");
    }
    else
    {
        var (endpoint, deployment) = ValidateAzureChatEndpoint(foundryEndpoint, foundryDeployment);
        var connection = new DbConnectionStringBuilder
        {
            ["Endpoint"] = endpoint.AbsoluteUri,
            ["Deployment"] = deployment
        };
        keylessChatConnectionString = connection.ConnectionString;
    }
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
    .WaitFor(taskflowDb);
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
    .WaitFor(taskflowDb)
    .WaitFor(redis);
api = WithAuditSink(api);
api = WithObjectStorage(api);
api = WithBroker(api);
api = WithReadModel(api);
api = WithLaneEnvironment(api);

// Wire the externally provisioned Azure Foundry chat model into the API.
if (chat is not null)
{
    api = api.WithReference(chat);
}
else if (azureFoundryRequested)
{
    api = api.WithEnvironment("ConnectionStrings__chat", keylessChatConnectionString);
}

if (isTesting)
    api = api.WithEnvironment("Cors__AllowedOrigins__0", "http://localhost");

// Test graphs and the manual Test.Load run (TASKFLOW_ASPIRE_LOAD_PROFILE=true on a dev stack) raise the scaffold
// tenant's standard tier. Every request in both authenticates as the one scaffold tenant, so the shipped 100
// requests/60 s budget would otherwise measure the limiter's 429s instead of the endpoints.
if (isTesting || Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_LOAD_PROFILE") == "true")
    api = api.WithEnvironment("RateLimiting__Tenants__Tiers__standard__PermitLimit", "10000");

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
if (isTesting)
    gateway = gateway.WithEnvironment("RateLimiting__Edge__Enabled", "false");
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

if (!isTesting || schedulerAvailableInTesting || fullLaneAvailableInTesting)
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
        // The ComplianceCheck job starts compliance-check in this host, so its workflow self-calls (the "taskflow-api"
        // FlowEngine client) leave from here and need the Api's address; unset, the client falls back to https://localhost.
        .WithEnvironment("FlowEngine__TaskFlowApiBaseUrl", api.GetEndpoint("http"))
        // Two replicas so the outbox/blob lease path is exercised locally (D-026): both drain, neither doubles
        // up. One replica under test so the graph boot stays inside the mesh startup budget.
        .WithReplicas(isTesting ? 1 : 2)
        .WaitForCompletion(migrator)
        .WaitFor(taskflowDb);
    scheduler = WithAuditSink(scheduler);
    // BlobDeleteWorkerService runs in Scheduler in both lanes, so it must receive the selected object store.
    scheduler = WithObjectStorage(scheduler);
    scheduler = WithBroker(scheduler);
    scheduler = WithReadModel(scheduler);
    scheduler = WithLaneEnvironment(scheduler);

    if (!string.IsNullOrWhiteSpace(applicationStyle))
    {
        scheduler.WithEnvironment("TASKFLOW_APPLICATION_STYLE", applicationStyle);
    }
}

if (!isTesting || reactAvailableInTesting || fullLaneAvailableInTesting)
{
    builder.AddViteApp("taskflowreact", "../../../UI/TaskFlow.React")
        .WithReference(gateway)
        .WithEnvironment("VITE_API_BASE_URL", gateway.GetEndpoint("http"))
        .WaitFor(gateway)
        .WithExternalHttpEndpoints();
}

if (!isTesting || unoWasmAvailableInTesting || fullLaneAvailableInTesting)
{
    var unoWasm = builder.AddProject<Projects.TaskFlow_Uno_WasmHost>("taskflowuno")
        .WithReference(gateway)
        .WithEnvironment("Gateway__BaseUrl", gateway.GetEndpoint("http"))
        .WaitFor(gateway)
        .WithExternalHttpEndpoints();

    var publishedDistPath = Environment.GetEnvironmentVariable("TASKFLOW_UNO_WASM_DIST_PATH");
    if (isTesting && !string.IsNullOrWhiteSpace(publishedDistPath))
    {
        unoWasm.WithEnvironment("UnoWasm__DistPath", publishedDistPath);
    }
}

// The Functions host is Azure-only (it needs the storage account for its own host state), and its consumer
// logic is already shared with the Scheduler's RabbitMQ handlers - so the NonAzure lane simply does not
// declare it rather than declaring a host that cannot run there (D-036).
if (!nonAzureLane && (!isTesting || functionsAvailableInTesting || fullLaneAvailableInTesting))
{
    // Functions host
    var functions = builder.AddAzureFunctionsProject<Projects.TaskFlow_Functions>("taskflowfunctions")
        .WithHostStorage(storage!)
        // The Functions host process emits request telemetry itself; suppress the worker's ASP.NET Core
        // instrumentation so requests are not double-reported when the Azure Monitor distro is active.
        .WithEnvironment("OpenTelemetry__SuppressAspNetCoreInstrumentation", "true")
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
        .WaitFor(taskflowDb)
        .WaitFor(storage!);
    functions = WithBroker(functions);
    functions = WithReadModel(functions);
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

    // Wire the externally provisioned Azure Foundry model into Functions for the AI readiness review (D6).
    if (chat is not null)
    {
        functions.WithReference(chat);
    }
    else if (azureFoundryRequested)
    {
        functions.WithEnvironment("ConnectionStrings__chat", keylessChatConnectionString);
    }
}

// One place decides how a host reaches the broker, so adding a host cannot forget the reference or the wait.
IResourceBuilder<T> WithBroker<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment, IResourceWithWaitSupport
{
    if (rabbitMq is not null)
        return host
            .WithReference(rabbitMq, connectionName: "RabbitMq1")
            .WithEnvironment("Messaging__RabbitMq__ConnectionString", rabbitMq.Resource.ConnectionStringExpression)
            .WaitFor(rabbitMq);

    return host.WithReference(serviceBus!).WaitFor(serviceBus!);
}

// Same rule for object storage (D-037): one place decides whether a host talks to the Azurite blob emulator
// or to SeaweedFS, so adding a host cannot forget the reference. SigV4 signs the Host header into a presigned
// URL, so ServiceUrl and PublicServiceUrl are the same allocated endpoint here - locally the app and the
// browser reach SeaweedFS through the same host-mapped address.
IResourceBuilder<T> WithObjectStorage<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment, IResourceWithWaitSupport
{
    if (seaweedFs is null)
        return host.WithReference(blobs!).WaitFor(storage!);

    return host
        .WithEnvironment("Storage__S3__ServiceUrl", seaweedFs.GetEndpoint("s3"))
        .WithEnvironment("Storage__S3__PublicServiceUrl", seaweedFs.GetEndpoint("s3"))
        .WithEnvironment("Storage__S3__AccessKeyId", s3AccessKey!)
        .WithEnvironment("Storage__S3__SecretAccessKey", s3SecretKey!)
        .WithEnvironment("Storage__S3__ForcePathStyle", "true")
        .WaitFor(seaweedFs);
}

// PostgreSQL JSONB needs no resource beyond taskflowdb. The explicit MongoDB arm adds one connection and
// one readiness edge to the hosts that project or query TaskView documents.
IResourceBuilder<T> WithReadModel<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment, IResourceWithWaitSupport
{
    if (cosmos is not null) return host.WithReference(cosmos).WaitFor(cosmos);
    if (!nonAzureLane && isTesting)
        return host.WithEnvironment("DOTNET_ENVIRONMENT", "Testing")
                   .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Testing")
                   .WithEnvironment("Testing__UseNoOpCosmosReadModel", "true");
    if (mongoDb is not null)
        return host.WithEnvironment("ConnectionStrings__MongoDb1", mongoDb.GetEndpoint("mongodb"))
                   .WaitFor(mongoDb);
    return host;
}

// D-060: every host learns the explicit lane and every resolved switch value this graph declared containers
// for, the same way Bicep and the compose lane set them, so no host falls back to the unset NonAzure default
// on its own.
IResourceBuilder<T> WithLaneEnvironment<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment
{
    foreach (var (key, value) in lane.HostEnvironment)
    {
        host = host.WithEnvironment(key, value);
    }

    return host;
}

// The Azure Table audit sink only exists in the Azure lane; the NonAzure lane audits relationally (D-039).
IResourceBuilder<T> WithAuditSink<T>(IResourceBuilder<T> host)
    where T : IResourceWithEnvironment =>
    tables is null ? host : host.WithReference(tables);

static void ValidateAzureChatConnectionString(string connectionString)
{
    DbConnectionStringBuilder parsed;
    try
    {
        parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };
    }
    catch (ArgumentException)
    {
        throw new InvalidOperationException(
            "ConnectionStrings:chat must be a valid connection string containing Endpoint and Deployment.");
    }

    _ = ValidateAzureChatEndpoint(
        parsed.TryGetValue("Endpoint", out var endpoint) ? Convert.ToString(endpoint) : null,
        parsed.TryGetValue("Deployment", out var deployment) ? Convert.ToString(deployment) : null,
        "ConnectionStrings:chat");
}

static (Uri Endpoint, string Deployment) ValidateAzureChatEndpoint(
    string? endpointValue,
    string? deploymentValue,
    string source = "AiServices")
{
    if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
        || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"{source} requires an absolute HTTPS Endpoint for externally provisioned Azure AI Foundry.");
    }

    if (string.IsNullOrWhiteSpace(deploymentValue))
    {
        throw new InvalidOperationException(
            $"{source} requires a non-empty Deployment. Configure AiServices:AgentModelDeployment or include Deployment in ConnectionStrings:chat.");
    }

    return (endpoint, deploymentValue.Trim());
}

// See the persistent-container note at the top of the infrastructure resources: point every endpoint of a
// persistent (proxyless) container at the IPv4 loopback the runtime publishes on.
static IResourceBuilder<T> UseIpv4Loopback<T>(IResourceBuilder<T> resource) where T : IResourceWithEndpoints
{
    foreach (var endpoint in resource.Resource.Annotations.OfType<EndpointAnnotation>())
        endpoint.TargetHost = "127.0.0.1";
    return resource;
}

await builder.Build().RunAsync();

// Required so Aspire test hosts (AspireTestHost, PlaywrightAspireHost) can resolve this AppHost via
// Type.GetType("Program, AppHost") for DistributedApplicationTestingBuilder. The .NET 10 auto-generated
// Program is internal, so the explicit public declaration is load-bearing here despite ASP0027.
#pragma warning disable ASP0027 // Public partial Program is required for cross-assembly reflection lookup.
/// <summary>Configures program host behavior for TaskFlow runtime services.</summary>
public partial class Program;
#pragma warning restore ASP0027
