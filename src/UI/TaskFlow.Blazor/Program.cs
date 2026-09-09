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
using TaskFlow.Contracts.Grpc;

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
var apiClient = builder.Services
    .AddRefitGeneratedClient<ITaskFlowApiClient>(new RefitSettings
    {
        ContentSerializer = new SystemTextJsonContentSerializer(jsonOptions)
    })
    .ConfigureHttpClient(client =>
    {
        client.BaseAddress = new Uri(gatewayBaseUrl);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    })
    .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear());

apiClient.AddStandardResilienceHandler();

// D-051: this is the read pipeline a rendered page waits on, so a slow tail costs a visibly stalled
// component. Hedging is applied here and nowhere else - the attachment upload client and the AI client below
// carry writes and a long-lived stream, neither of which is safe or useful to duplicate.
apiClient.AddReadHedging(builder.Configuration);

// D-054: the internal gRPC read client - the one in-cluster service-to-service hop. Every public client
// still goes through the gateway over REST.
//
// The address is always a concrete URL from configuration: the AppHost injects it from the Api's named
// "Grpc" endpoint, Bicep composes it from the Api container app's internal FQDN, and the compose lane
// sets the same Grpc__TaskFlowRead__Address variable. Service discovery is deliberately NOT used here -
// the handler that resolves a "http://_grpc.taskflowapi" name is one of the inherited additional handlers
// this host clears below, so a name-shaped address would reach the resolver that is no longer there.
//
// This host also sends no credentials, exactly like the Refit clients above (see the note at their
// registration): the Api authenticates every request with its own scheme, so the tenant a gRPC read sees
// is the tenant the REST read sees. When a real identity provider replaces the scaffold handler, the
// token handler goes on both clients together.
const string GrpcReadPlaceholderAddress = "http://127.0.0.1:0";
var grpcReadAddress = builder.Configuration["Grpc:TaskFlowRead:Address"];
var useGrpcReads = builder.Configuration.GetValue("Clients:UseGrpcReads", grpcReadAddress is not null);

if (useGrpcReads && grpcReadAddress is null)
{
    throw new InvalidOperationException(
        "Clients:UseGrpcReads is enabled but Grpc:TaskFlowRead:Address is not configured. Set it, or run " +
        "under the AppHost, which injects the taskflowapi 'Grpc' endpoint address.");
}

builder.Services.AddSingleton(new ClientReadSettings(useGrpcReads));

// Registered unconditionally so the pages can inject the client the same way they inject the Refit one.
// With no address configured the flag above is off and the client is constructed but never called; the
// placeholder is an unroutable loopback rather than a plausible host, so a future call site that forgot
// the flag fails immediately instead of quietly reaching something else.
builder.Services
    .AddGrpcClient<TaskFlowRead.TaskFlowReadClient>(options =>
        options.Address = new Uri(grpcReadAddress ?? GrpcReadPlaceholderAddress))
    // Same reason as the Refit clients above: ServiceDefaults adds header propagation to every HttpClient
    // through ConfigureHttpClientDefaults, and this host runs no UseHeaderPropagation middleware, so an
    // inherited handler would throw on every call from inside a SignalR circuit.
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

// No HTTPS-redirect middleware here: every deployment lane terminates TLS at the edge (Container Apps
// ingress in the Azure lane, Caddy in the portable lane, D-036/D-049), so this container only ever serves
// plain http, same as the Api and Gateway hosts.
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
