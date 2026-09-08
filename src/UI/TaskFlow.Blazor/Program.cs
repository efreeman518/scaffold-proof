using EF.FlowEngine.Dashboard;
using MudBlazor;
using MudBlazor.Services;
using Refit;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TaskFlow.Application.Models.Serialization;
using TaskFlow.ApiClient;
using TaskFlow.Blazor.Components;
using TaskFlow.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Shared Aspire service defaults: OpenTelemetry (incl. Azure Monitor when configured), health
// checks, service discovery, and HTTP resilience. Keeps this server-hosted UI participating in
// the same telemetry pipeline as the backend hosts while still running with no Azure config.
builder.AddServiceDefaults();
builder.AddProxyForwarding();

// Blazor Server host for CRUD pages and FlowEngine dashboard pages. API calls go through
// the gateway so auth, claim forwarding, and routing match the other front ends.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = true;
    config.SnackbarConfiguration.VisibleStateDuration = 4000;
});

builder.Services.AddScoped<FloatService>();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() }
};
// D-048: generated metadata for the API DTOs this circuit deserializes on every interaction, with the
// reflection resolver kept behind it for anything the context does not cover. Naming stays whatever these
// options say, so the request/response format is unchanged.
jsonOptions.TypeInfoResolverChain.Insert(0, TaskFlowJsonContext.Default);
jsonOptions.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());

var gatewayBaseUrl = builder.Configuration["Gateway:BaseUrl"]
    ?? throw new InvalidOperationException("Gateway:BaseUrl not configured.");

// AddServiceDefaults applies AddHeaderPropagation() to EVERY HttpClient via ConfigureHttpClientDefaults.
// That handler only works behind UseHeaderPropagation() middleware, which this Blazor Server app does not
// run - and outbound API calls happen inside the interactive SignalR circuit, outside any HTTP request
// scope. As a result HeaderPropagationValues.Headers is unset and the handler throws on every request; the
// globally-added standard resilience handler then retries that failure until its 30s total timeout, so the
// call hangs and FloatService silently swallows the resulting cancellation (no error shown, page never
// navigates). Clear the inherited additional handlers and add a single clean resilience handler instead.
// No auth handler yet - gateway dev mode accepts unauthenticated requests.
builder.Services
    .AddRefitGeneratedClient<ITaskFlowApiClient>(new RefitSettings
    {
        ContentSerializer = new SystemTextJsonContentSerializer(jsonOptions)
    })
    .ConfigureHttpClient(client =>
    {
        client.BaseAddress = new Uri(gatewayBaseUrl);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    })
    .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear())
    .AddStandardResilienceHandler();

// Attachment upload is a request shape (StreamPart) the Refit source generator cannot build (RF006),
// so it lives on its own interface registered via the reflection-based AddRefitClient rather than
// AddRefitGeneratedClient. Same gateway base address and handler pipeline as the generated client.
builder.Services
    .AddRefitClient<IAttachmentUploadClient>(new RefitSettings
    {
        ContentSerializer = new SystemTextJsonContentSerializer(jsonOptions)
    })
    .ConfigureHttpClient(client =>
    {
        client.BaseAddress = new Uri(gatewayBaseUrl);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    })
    .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear())
    .AddStandardResilienceHandler();

// Raw HTTP client for the AI demo endpoints (the typed Refit client does not cover the AI routes,
// and the streaming chat demo needs raw Server-Sent Events). Points at the gateway like the others.
// Same reasoning as above: drop the inherited header-propagation handler so streaming calls don't hang.
builder.Services.AddHttpClient("TaskFlowAi", client =>
{
    client.BaseAddress = new Uri(gatewayBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(2);
})
.ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear());

// FlowEngine Dashboard - talks to TaskFlow.Api's MapFlowEngineAdmin via the gateway.
// Pages contributed by the package are picked up via Routes.razor's AdditionalAssemblies.
var flowEngineAdminBaseUrl = builder.Configuration["FlowEngine:AdminApiBaseUrl"]
    ?? new Uri(new Uri(gatewayBaseUrl), "/api/flowengine/").ToString();
builder.Services.AddFlowEngineDashboard(adminApiBaseUrl: flowEngineAdminBaseUrl);

var app = builder.Build();

app.UseProxyForwarding();
app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
