using TaskFlow.Bootstrapper;
using TaskFlow.Scheduler;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Telemetry;
using TickerQ.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// D-034: only the selected provider opens a connection. The Aspire client integration registers the singleton
// IConnection; EF.Messaging.RabbitMq reuses it and never disposes a connection it did not create.
if (RegisterServices.ResolveMessagingProvider(builder.Configuration) == MessagingProvider.RabbitMq)
{
    builder.AddRabbitMQClient("RabbitMq1");
}

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(
        SchedulingMetrics.MeterName, MessagingMetrics.MeterName, "EF.Messaging.RabbitMq"));
builder.Services
    .RegisterInfrastructureServices(builder.Configuration)
    .RegisterApplicationServices(builder.Configuration)
    .AddSchedulerServices(builder.Configuration);
builder.AddTickerQConfig();

var app = builder.Build();

await app.ValidateTickerQDatabase();

app.UseTickerQ();
app.MapDefaultEndpoints();

await app.SeedCronJobs();

await app.RunAsync();
