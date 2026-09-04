using AppHost;
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
// Opt-in only: normal Aspire tests keep AI deterministic by disabling Foundry Local.
// Set before AppHost build only for manual local-AI AppHost runs.
var foundryLocalAvailableInTesting =
    Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_ENABLE_FOUNDRY_LOCAL") == "true";

// Keep the database password stable across restarts so persistent volumes remain usable.
// Tests can still override via Parameters__sql-password / Parameters__postgres-password.
var defaultSqlPassword = LocalSqlSettings.SharedSaPassword;
var sqlServerImageTag = "2025-latest";

// D-020: exactly one relational server runs locally, chosen by TASKFLOW_DB_PROVIDER / Database:Provider
// (default SqlServer). Every host receives the same choice as Database__Provider so UseTaskFlowProvider agrees.
var dbProviderName = Environment.GetEnvironmentVariable("TASKFLOW_DB_PROVIDER")
    ?? builder.Configuration["Database:Provider"]
    ?? "SqlServer";
var usePostgres = string.Equals(dbProviderName, "PostgreSql", StringComparison.OrdinalIgnoreCase);

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

// Azure Storage (Blob) - emulator
// Not using ContainerLifetime.Persistent - persistent emulator containers survive Aspire restarts
// but get stranded on deleted Podman networks, causing netavark "eth2 already exists" errors.
var storage = builder.AddAzureStorage("AzureStorage")
    .RunAsEmulator(emulator => emulator.WithImageTag("latest"));
var blobs = storage.AddBlobs("BlobStorage1");
var tables = storage.AddTables("TableStorage1");

// Azure Service Bus - emulator
var serviceBus = builder.AddAzureServiceBus("ServiceBus1")
    .RunAsEmulator(emulator => emulator.WithImageTag("latest"));
var domainEventsTopic = serviceBus.AddServiceBusTopic("DomainEvents");
domainEventsTopic.AddServiceBusSubscription("function-processor");
serviceBus.AddServiceBusQueue("TaskCommands");

// The Service Bus emulator bundles its own SQL Server sidecar (ServiceBus1-mssql); the Aspire package
// hardcodes that image, and RunAsEmulator's callback cannot reach it. Override it here so it matches
// the `sql` container tag and lets Docker share layers instead of pulling a second SQL Server major version.
builder.CreateResourceBuilder(
        (ContainerResource)builder.Resources.Single(r => r.Name == "ServiceBus1-mssql"))
    .WithImageTag(sqlServerImageTag);

// Azure Cosmos DB - emulator (see AzureStorage comment re: Persistent lifetime)
// Skipped in Testing: the emulator is heavy (~1.3 GB) and not needed for audit pipeline tests.
// The API's AddCosmosDbServices falls back to NoOpTaskViewRepository when the connection string is absent.
if (!isTesting)
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
IResourceBuilder<FoundryDeploymentResource>? chat = null;
var azureFoundryConfigured = builder.ExecutionContext.IsPublishMode
    || !string.IsNullOrWhiteSpace(builder.Configuration["AiServices:FoundryEndpoint"])
    || Environment.GetEnvironmentVariable("TASKFLOW_USE_AZURE_FOUNDRY") == "true";

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

// API host
var api = builder.AddProject<Projects.TaskFlow_Api>("taskflowapi")
    .WithReference(taskflowDb, connectionName: "TaskFlowDbContextTrxn")
    .WithReference(taskflowDb, connectionName: "TaskFlowDbContextQuery")
    .WithReference(taskflowDb, connectionName: "TaskFlowFlowEngineDbContext")
    .WithReference(redis, connectionName: "Redis1")
    .WithReference(tables)
    .WithReference(blobs)
    .WithReference(serviceBus)
    .WithEnvironment("Database__Provider", dbProviderName)
    .WithEnvironment("Database__Encryption__LocalKeyBase64", columnEncryptionKey)
    .WithEnvironment("Database__Encryption__BlindIndexKeyBase64", blindIndexKey)
    .WaitForCompletion(migrator)
    .WaitFor(dbServer)
    .WaitFor(redis)
    .WaitFor(serviceBus);

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

builder.AddProject<Projects.TaskFlow_Blazor>("taskflowblazor")
    .WithReference(gateway)
    .WithEnvironment("Gateway__BaseUrl", gateway.GetEndpoint("http"))
    .WaitFor(gateway)
    .WithExternalHttpEndpoints();

if (!isTesting)
{
    // Scheduler host
    var scheduler = builder.AddProject<Projects.TaskFlow_Scheduler>("taskflowscheduler")
        .WithReference(taskflowDb, connectionName: "TaskFlowDbContextTrxn")
        .WithReference(taskflowDb, connectionName: "TaskFlowDbContextQuery")
        .WithReference(taskflowDb, connectionName: "TaskFlowFlowEngineDbContext")
        .WithReference(taskflowDb, connectionName: "TickerQDbContext")
        .WithReference(redis, connectionName: "Redis1")
        .WithReference(tables)
        .WithReference(serviceBus)
        .WithEnvironment("Database__Provider", dbProviderName)
        .WithEnvironment("Database__Encryption__LocalKeyBase64", columnEncryptionKey)
        .WithEnvironment("Database__Encryption__BlindIndexKeyBase64", blindIndexKey)
        .WithReplicas(1)
        .WaitForCompletion(migrator)
        .WaitFor(dbServer)
        .WaitFor(serviceBus);

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

if (!isTesting || functionsAvailableInTesting)
{
    // Functions host
    var functions = builder.AddAzureFunctionsProject<Projects.TaskFlow_Functions>("taskflowfunctions")
        .WithHostStorage(storage)
        // The Functions host process emits request telemetry itself; suppress the worker's ASP.NET Core
        // instrumentation so requests are not double-reported when the Azure Monitor distro is active.
        .WithEnvironment("TASKFLOW_SUPPRESS_ASPNETCORE_INSTRUMENTATION", "true")
        .WithReference(taskflowDb, connectionName: "TaskFlowDbContextTrxn")
        .WithReference(taskflowDb, connectionName: "TaskFlowDbContextQuery")
        .WithReference(taskflowDb, connectionName: "TaskFlowFlowEngineDbContext")
        .WithReference(tables)
        .WithReference(blobs)
        .WithReference(serviceBus)
        .WithEnvironment("Database__Provider", dbProviderName)
        .WithEnvironment("Database__Encryption__LocalKeyBase64", columnEncryptionKey)
        .WithEnvironment("Database__Encryption__BlindIndexKeyBase64", blindIndexKey)
        .WaitForCompletion(migrator)
        .WaitFor(dbServer)
        .WaitFor(storage)
        .WaitFor(serviceBus);

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

await builder.Build().RunAsync();

// Required so Aspire test hosts (AspireTestHost, PlaywrightAspireHost) can resolve this AppHost via
// Type.GetType("Program, AppHost") for DistributedApplicationTestingBuilder. The .NET 10 auto-generated
// Program is internal, so the explicit public declaration is load-bearing here despite ASP0027.
#pragma warning disable ASP0027 // Public partial Program is required for cross-assembly reflection lookup.
/// <summary>Configures program host behavior for TaskFlow runtime services.</summary>
public partial class Program;
#pragma warning restore ASP0027
