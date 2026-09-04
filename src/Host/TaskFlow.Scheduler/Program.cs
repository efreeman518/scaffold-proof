using TaskFlow.Bootstrapper;
using TaskFlow.Scheduler;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Telemetry;
using TickerQ.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(SchedulingMetrics.MeterName, MessagingMetrics.MeterName));
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
