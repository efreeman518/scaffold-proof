using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TaskFlow.Application.Models.Serialization;
using TaskFlow.Bootstrapper;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.AddServiceDefaults();

var startupLogger = LoggerFactory
    .Create(logging => logging.AddConsole())
    .CreateLogger("TaskFlow.Functions");
await builder.RegisterAiChatClientAsync(startupLogger);

// D-048: source-generated metadata for the DTOs the HTTP-triggered functions read and write, with the
// reflection resolver kept behind it - the triggers also serialize anonymous error shapes, which only
// reflection can handle. The options are otherwise the worker's own defaults (verified against
// WorkerOptions resolved from AddFunctionsWorkerCore: no naming policy, case-sensitive, no converters), so
// the request and response format is unchanged; this registration runs after the built-in setup and wins.
builder.Services.Configure<WorkerOptions>(options =>
{
    var json = new JsonSerializerOptions();
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
app.AutoRegisterMessageHandlers();
await app.RunAsync();
