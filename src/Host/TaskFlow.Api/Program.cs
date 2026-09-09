using EF.Common;
using TaskFlow.Api;
using TaskFlow.Bootstrapper;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var services = builder.Services;
var appName = config.GetValue<string>("AppName") ?? "TaskFlow.Api";
var env = config.GetValue<string>("ASPNETCORE_ENVIRONMENT")
    ?? config.GetValue<string>("DOTNET_ENVIRONMENT") ?? "Undefined";

ILogger<Program> startupLogger = CreateStartupLogger();
startupLogger.Startup(appName, env);

try
{
    // 0. Azure App Configuration (D-042): dynamic config + feature flags, no-op unless AppConfig:Endpoint
    // (or ConnectionStrings:AppConfig) is set. Runs first so every later configuration read - including
    // AddServiceDefaults below - can see values it overrides.
    builder.AddTaskFlowAppConfiguration();

    // 1. Service defaults (OpenTelemetry, health, resilience)
    builder.AddServiceDefaults();
    builder.AddProxyForwarding();

    // 2. Data Protection (key-ring persistence + Key Vault key encryption, D-043)
    builder.AddTaskFlowDataProtection(startupLogger);

    // 2b. AI chat client: shared Azure -> Foundry Local -> no-op strategy.
    await builder.RegisterAiChatClientAsync(startupLogger);

    // 3. Registration chain - order matters for dependency resolution
    services
        .RegisterInfrastructureServices(config)
        .RegisterDomainServices(config)
        .RegisterApplicationServices(config)
        .RegisterBackgroundServices(config)
        .AddApiServices(config, startupLogger);

    // 4. Build + pipeline
    var app = builder.Build().ConfigurePipeline();

    // 5. Startup tasks (migrations, warmup)
    await app.RunStartupTasks();

    // 6. Switch to runtime logger
    StaticLogging.SetStaticLoggerFactory(app.Services.GetRequiredService<ILoggerFactory>());

    await app.RunAsync();
}
catch (Exception ex)
{
    startupLogger.HostTerminated(ex, appName, env);
}
finally
{
    startupLogger.EndingApplication(appName, env);
}

ILogger<Program> CreateStartupLogger()
{
    StaticLogging.CreateStaticLoggerFactory(logBuilder =>
    {
        logBuilder.SetMinimumLevel(LogLevel.Information);
        logBuilder.AddConsole();
    });
    return StaticLogging.CreateLogger<Program>();
}

// Required so cross-assembly integration/smoke test projects (Test.Endpoints, Test.FoundryLocal,
// Test.E2E, ...) can reference this host as WebApplicationFactory<Program>. The .NET 10 auto-generated
// Program is internal, so the explicit public declaration is load-bearing here despite ASP0027.
#pragma warning disable ASP0027 // Public partial Program is required for external WebApplicationFactory access.
public partial class Program { }
#pragma warning restore ASP0027
