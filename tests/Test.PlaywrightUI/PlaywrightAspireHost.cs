using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Test.Support.Aspire;

namespace Test.PlaywrightUI.Hosting;

/// <summary>
/// Starts AppHost test mesh exposes browser endpoints for C# TypeScript Playwright tests.
/// </summary>
internal sealed class PlaywrightAspireHost : IAsyncDisposable
{
    private const string GatewayResourceName = "taskflowgateway";
    private const string BlazorResourceName = "taskflowblazor";
    private const string ReactResourceName = "taskflowreact";
    private const string HttpEndpointName = "http";
    private const string ResourceLoggingEnvironmentVariable = "TASKFLOW_ASPIRE_RESOURCE_LOGGING";
    private const string UnoColdStartProject = "uno-release-cold-start";
    private static readonly string[] DiagnosticResourceNames =
    [
        GatewayResourceName,
        BlazorResourceName,
        ReactResourceName,
        "taskflowuno",
        "taskflowapi",
        "taskflowdb",
        "taskflowmigrator"
    ];

    private readonly AspireTestHostContext _hostContext;
    private readonly Dictionary<string, string?> _originalEnvironment;

    private PlaywrightAspireHost(
        AspireTestHostContext hostContext,
        string gatewayBaseUrl,
        string blazorBaseUrl,
        IReadOnlyList<string> typeScriptProjects,
        IReadOnlyList<string> diagnosticMessages,
        Dictionary<string, string?> originalEnvironment)
    {
        _hostContext = hostContext;
        GatewayBaseUrl = gatewayBaseUrl.TrimEnd('/');
        BlazorBaseUrl = blazorBaseUrl.TrimEnd('/');
        TypeScriptProjects = typeScriptProjects;
        DiagnosticMessages = diagnosticMessages;
        _originalEnvironment = originalEnvironment;
    }

    internal string GatewayBaseUrl { get; }

    internal string BlazorBaseUrl { get; }

    internal IReadOnlyList<string> TypeScriptProjects { get; }

    internal IReadOnlyList<string> DiagnosticMessages { get; }

    private static readonly string[] DefaultProjects = ["blazor", "react", "uno"];

    internal static Task<PlaywrightAspireHost> StartAsync(CancellationToken ct)
        => StartAsync(DefaultProjects, ct);

    internal static async Task<PlaywrightAspireHost> StartAsync(IReadOnlyCollection<string> requestedProjects, CancellationToken ct)
    {
        var requestedProjectSet = requestedProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wantsBlazor = requestedProjectSet.Contains("blazor");
        var wantsReact = requestedProjectSet.Contains("react");
        var wantsUnoSmoke = requestedProjectSet.Contains("uno");
        var wantsUnoColdStart = requestedProjectSet.Contains(UnoColdStartProject);
        var wantsUno = wantsUnoSmoke || wantsUnoColdStart;
        var startupTimeoutVariable = wantsUno
            ? "TASKFLOW_WASM_STARTUP_TIMEOUT_SECONDS"
            : "TASKFLOW_ASPIRE_STARTUP_TIMEOUT_SECONDS";
        var hostContext = new AspireTestHostContext(
            AspireTestHostContext.ReadPositiveSeconds(startupTimeoutVariable, wantsUno ? 1_800 : 900),
            ResourceLoggingEnvironmentVariable);

        var originalEnvironment = CaptureEnvironment(
            "TASKFLOW_ASPIRE_TESTING",
            "TASKFLOW_ASPIRE_REACT_AVAILABLE",
            "TASKFLOW_ASPIRE_UNO_WASM_AVAILABLE",
            "TASKFLOW_UNO_WASM_DIST_PATH",
            "PLAYWRIGHT_GATEWAY_URL",
            "PLAYWRIGHT_BLAZOR_URL",
            "PLAYWRIGHT_REACT_URL",
            "PLAYWRIGHT_UNO_URL");

        DistributedApplication? app = null;
        try
        {
            var dockerUnavailableReason = await hostContext.GetDockerUnavailableReasonAsync(ct);
            if (dockerUnavailableReason is not null)
                throw new DockerUnavailableException(dockerUnavailableReason);

            var reactRunnable = wantsReact && IsReactRunnable();
            var unoTarget = wantsUno
                ? await WasmAppHost.PrepareAsync(hostContext, wantsUnoColdStart, ct)
                : new WasmHostTarget(false, false, "Uno WASM project not requested.");
            var diagnostics = new List<string> { unoTarget.Message };

            Environment.SetEnvironmentVariable("TASKFLOW_ASPIRE_TESTING", "true");
            SetOrClear("TASKFLOW_ASPIRE_REACT_AVAILABLE", reactRunnable);
            SetOrClear("TASKFLOW_ASPIRE_UNO_WASM_AVAILABLE", unoTarget.HostWithAspire);

            var appHostProgramType = Type.GetType("Program, AppHost", throwOnError: true)!;
            var builder = await hostContext.RunStartupStepAsync(
                "create Playwright Aspire test host",
                token => DistributedApplicationTestingBuilder.CreateAsync(
                    appHostProgramType,
                    args: [],
                    configureBuilder: (appOptions, _) =>
                    {
                        appOptions.DisableDashboard = true;
                        appOptions.EnableResourceLogging = hostContext.ResourceLoggingEnabled;
                    },
                    cancellationToken: token),
                ct);

            builder.Services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                logging.AddFilter("Aspire.", LogLevel.Warning);
            });

            app = await hostContext.RunStartupStepAsync(
                "build Playwright Aspire test host",
                token => builder.BuildAsync(token),
                ct);
            hostContext.Attach(app);
            await hostContext.RunStartupStepAsync(
                "start Playwright Aspire test host",
                token => app.StartAsync(token),
                ct);

            var gatewayBaseUrl = await ResolveEndpointAsync(
                app,
                hostContext,
                GatewayResourceName,
                "PLAYWRIGHT_GATEWAY_URL",
                ct);

            var blazorBaseUrl = string.Empty;
            if (wantsBlazor)
            {
                blazorBaseUrl = await ResolveEndpointAsync(
                    app,
                    hostContext,
                    BlazorResourceName,
                    "PLAYWRIGHT_BLAZOR_URL",
                    ct);
            }

            var typeScriptProjects = new List<string>();
            if (wantsBlazor)
            {
                typeScriptProjects.Add("blazor");
            }

            if (wantsReact && (HasValue("PLAYWRIGHT_REACT_URL") || HasValue("TASKFLOW_REACT_BASE_URL")))
            {
                var reactBaseUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_REACT_URL")
                    ?? Environment.GetEnvironmentVariable("TASKFLOW_REACT_BASE_URL");
                Environment.SetEnvironmentVariable("PLAYWRIGHT_REACT_URL", reactBaseUrl?.TrimEnd('/'));
                typeScriptProjects.Add("react");
            }
            else if (reactRunnable)
            {
                await ResolveEndpointAsync(app, hostContext, ReactResourceName, "PLAYWRIGHT_REACT_URL", ct);
                typeScriptProjects.Add("react");
            }

            if (unoTarget.RunTypeScriptProject)
            {
                if (unoTarget.HostWithAspire)
                {
                    var unoBaseUrl = await WasmAppHost.ResolveEndpointAsync(hostContext, app, ct);
                    diagnostics.Add($"Uno WASM hosted by Aspire at {unoBaseUrl}.");
                }

                if (wantsUnoSmoke)
                {
                    typeScriptProjects.Add("uno");
                }

                if (wantsUnoColdStart)
                {
                    typeScriptProjects.Add(UnoColdStartProject);
                }
            }

            return new PlaywrightAspireHost(
                hostContext,
                gatewayBaseUrl,
                blazorBaseUrl,
                typeScriptProjects,
                diagnostics,
                originalEnvironment);
        }
        catch (DockerUnavailableException)
        {
            RestoreEnvironment(originalEnvironment);
            throw;
        }
        catch (Exception ex)
        {
            var resourcesUnavailable = app is not null
                && ex is DistributedApplicationException
                && ResourcesFailedBeforeLaunch(app);

            if (app is not null)
            {
                foreach (var resourceName in DiagnosticResourceNames)
                {
                    await hostContext.DumpResourceDiagnosticsAsync(resourceName, CancellationToken.None);
                }

                try
                {
                    await hostContext.StopAndDisposeAsync(CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    Console.Error.WriteLine($"Aspire cleanup after startup failure also failed: {cleanupException.Message}");
                }
            }

            RestoreEnvironment(originalEnvironment);
            if (resourcesUnavailable)
                throw new ResourceUnavailableException(
                    "Aspire could not launch the test resources. No application process started.", ex);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _hostContext.StopAndDisposeAsync(CancellationToken.None);
        }
        finally
        {
            RestoreEnvironment(_originalEnvironment);
        }
    }

    internal async Task<T> RunWithinStartupBudgetAsync<T>(
        string stepName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _hostContext.RunStartupStepAsync(stepName, operation, cancellationToken);
        }
        catch
        {
            await DumpDiagnosticsAsync(CancellationToken.None);
            throw;
        }
    }

    internal async Task RunWithinStartupBudgetAsync(
        string stepName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _hostContext.RunStartupStepAsync(stepName, operation, cancellationToken);
        }
        catch
        {
            await DumpDiagnosticsAsync(CancellationToken.None);
            throw;
        }
    }

    internal async Task DumpDiagnosticsAsync(CancellationToken cancellationToken)
    {
        foreach (var resourceName in DiagnosticResourceNames)
        {
            await _hostContext.DumpResourceDiagnosticsAsync(resourceName, cancellationToken);
        }
    }

    private static async Task<string> ResolveEndpointAsync(
        DistributedApplication app,
        AspireTestHostContext hostContext,
        string resourceName,
        string environmentVariableName,
        CancellationToken ct)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        await hostContext.WaitForResourceHealthyAsync(resourceName, ct);

        var endpoint = app.GetEndpoint(resourceName, HttpEndpointName).ToString().TrimEnd('/');
        Environment.SetEnvironmentVariable(environmentVariableName, endpoint);
        return endpoint;
    }

    private static Dictionary<string, string?> CaptureEnvironment(params string[] names) =>
        names.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static void SetOrClear(string name, bool enabled) =>
        Environment.SetEnvironmentVariable(name, enabled ? "true" : null);

    private static bool HasValue(string variableName) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));

    private static bool ResourcesFailedBeforeLaunch(DistributedApplication app)
    {
        var observed = DiagnosticResourceNames
            .Select(name => app.ResourceNotifications.TryGetCurrentState(name, out var resourceEvent)
                ? (resourceEvent.Snapshot.State?.Text, HasExitCode: resourceEvent.Snapshot.ExitCode is not null)
                : default)
            .Where(resource => resource.Text is not null)
            .ToArray();

        return observed.Length >= 2
            && observed.All(resource =>
                resource.Text!.Equals("FailedToStart", StringComparison.OrdinalIgnoreCase)
                && !resource.HasExitCode);
    }

    private static bool IsReactRunnable()
    {
        var reactRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src",
            "UI",
            "TaskFlow.React"));
        var viteShim = OperatingSystem.IsWindows()
            ? Path.Combine(reactRoot, "node_modules", ".bin", "vite.cmd")
            : Path.Combine(reactRoot, "node_modules", ".bin", "vite");

        return File.Exists(Path.Combine(reactRoot, "package.json"))
            && File.Exists(viteShim);
    }

    internal sealed class DockerUnavailableException(string message) : Exception(message);

    internal sealed class ResourceUnavailableException(string message, Exception innerException)
        : Exception(message, innerException);
}
