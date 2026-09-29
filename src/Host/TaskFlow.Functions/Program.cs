using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TaskFlow.Application.Models.Serialization;
using TaskFlow.Bootstrapper;
using TaskFlow.Functions;

var builder = FunctionsApplication.CreateBuilder(args);

// Azure App Configuration (D-042) parity with the other hosts: dynamic config + feature flags, no-op
// unless AppConfig:Endpoint (or ConnectionStrings:AppConfig) is set. Runs first so every later
// configuration read sees values it overrides; EF.Host refreshes it in the background, so the worker
// needs no refresh middleware.
builder.AddTaskFlowAppConfiguration();

builder.ConfigureFunctionsWebApplication();
// After the HTTP proxying middleware ConfigureFunctionsWebApplication registers: HTTP triggers resolve the
// request context against their HttpContext (anonymous, no tenant), everything else gets the system identity.
builder.UseMiddleware<HttpContextAccessorMiddleware>();

// Service Bus triggers execute outside an HTTP request; the correlation handler sends no header there and
// never throws, so explicit complete/dead-letter settlement through the worker's internal gRPC client is unaffected.
builder.AddServiceDefaults();

var startupLogger = LoggerFactory
    .Create(logging => logging.AddConsole())
    .CreateLogger("TaskFlow.Functions");
await builder.RegisterAiChatClientAsync(startupLogger);

// D-048: source-generated metadata for the DTOs the HTTP-triggered functions read and write, with the
// reflection resolver kept behind it - the triggers also serialize anonymous error shapes, which only
// reflection can handle. Web defaults keep HTTP payloads camelCase and case-insensitive, matching the API
// and clients; this registration runs after the built-in setup and wins.
builder.Services.Configure<WorkerOptions>(options =>
{
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    json.TypeInfoResolverChain.Insert(0, TaskFlowJsonContext.Default);
    json.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
    options.Serializer = new JsonObjectSerializer(json);
});

builder.Services
    .RegisterInfrastructureServices(builder.Configuration)
    .RegisterDomainServices(builder.Configuration)
    .RegisterApplicationServices(builder.Configuration)
    .RegisterBackgroundServices(builder.Configuration);

var app = builder.Build();
// Provision shared external resources before RunAsync activates Service Bus projection triggers.
await app.RunStartupTasks();
await app.RunAsync();
