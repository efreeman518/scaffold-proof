using TaskFlow.Bootstrapper;
using TaskFlow.Scheduler;
using TaskFlow.Scheduler.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Azure App Configuration (D-042): dynamic config + feature flags, no-op unless AppConfig:Endpoint
// (or ConnectionStrings:AppConfig) is set. Runs first so later configuration reads see values it overrides.
builder.AddTaskFlowAppConfiguration();

builder.AddServiceDefaults();

// D-034: only the selected provider opens a connection. The Aspire client integration registers the singleton
// IConnection; EF.Messaging.RabbitMq reuses it and never disposes a connection it did not create.
if (RegisterServices.ResolveMessagingProvider(builder.Configuration) == MessagingProvider.RabbitMq)
{
    builder.AddRabbitMQClient("RabbitMq1");
}

builder.Services.AddSchedulerOpenTelemetry(builder.Configuration);
builder.Services
    .RegisterInfrastructureServices(builder.Configuration)
    .RegisterApplicationServices(builder.Configuration)
    .AddSchedulerServices(builder.Configuration);
builder.AddJobScheduler();

var app = builder.Build();

// Provision shared external resources before RunAsync starts the RabbitMQ projection consumer.
await app.RunStartupTasks();

await app.ValidateSchedulerDatabase();

app.MapDefaultEndpoints();

// The cron planner and job worker registered by AddJobScheduler start with the host here.
await app.RunAsync();
