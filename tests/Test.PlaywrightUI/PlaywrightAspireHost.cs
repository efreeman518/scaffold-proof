using Aspire.Hosting;
using Aspire.Hosting.Testing;
using EF.IntegrationTesting.Aspire;
using EF.Testing.Environment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    private readonly EnvironmentVariableScope _environment;

    private PlaywrightAspireHost(
        AspireTestHostContext hostContext,
        string gatewayBaseUrl,
        string blazorBaseUrl,
        IReadOnlyList<string> typeScriptProjects,
        IReadOnlyList<string> diagnosticMessages,
        EnvironmentVariableScope environment)
    {
        _hostContext = hostContext;
        GatewayBaseUrl = gatewayBaseUrl.TrimEnd('/');
        BlazorBaseUrl = blazorBaseUrl.TrimEnd('/');
        TypeScriptProjects = typeScriptProjects;
        DiagnosticMessages = diagnosticMessages;
        _environment = environment;
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
            TestEnvironment.GetPositiveSeconds(startupTimeoutVariable, TimeSpan.FromSeconds(wantsUno ? 1_800 : 900)),
            new AspireTestHostOptions { IncludeResourceLogs = TestEnvironment.IsTrue(ResourceLoggingEnvironmentVariable) });

        // Every variable this host (and WasmAppHost) writes goes through this one scope, which restores the
        // originals when the host is disposed or its startup fails.
        var environment = new EnvironmentVariableScope();

        DistributedApplication? app = null;
        try
        {
            var dockerUnavailableReason = await hostContext.GetDockerUnavailableReasonAsync(ct);
            if (dockerUnavailableReason is not null)
                throw new DockerUnavailableException(dockerUnavailableReason);

            var reactRunnable = wantsReact && IsReactRunnable();
            var unoTarget = wantsUno
                ? await WasmAppHost.PrepareAsync(hostContext, environment, wantsUnoColdStart, ct)
                : new WasmHostTarget(false, false, "Uno WASM project not requested.");
            var diagnostics = new List<string> { unoTarget.Message };

            environment
                .Set("TASKFLOW_ASPIRE_TESTING", "true")
                .Set("TASKFLOW_ASPIRE_REACT_AVAILABLE", reactRunnable ? "true" : null)
                .Set("TASKFLOW_ASPIRE_UNO_WASM_AVAILABLE", unoTarget.HostWithAspire ? "true" : null);

            var appHostProgramType = Type.GetType("Program, AppHost", throwOnError: true)!;
            var builder = await hostContext.RunStartupStepAsync(
                "create Playwright Aspire test host",
                token => DistributedApplicationTestingBuilder.CreateAsync(
                    appHostProgramType,
                    args: [],
                    configureBuilder: (appOptions, _) =>
                    {
                        appOptions.DisableDashboard = true;
                        appOptions.EnableResourceLogging = hostContext.IncludeResourceLogs;
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
                environment,
                GatewayResourceName,
                "PLAYWRIGHT_GATEWAY_URL",
                ct);

            var blazorBaseUrl = string.Empty;
            if (wantsBlazor)
            {
                blazorBaseUrl = await ResolveEndpointAsync(
                    app,
                    hostContext,
                    environment,
                    BlazorResourceName,
                    "PLAYWRIGHT_BLAZOR_URL",
                    ct);
            }

            var typeScriptProjects = new List<string>();
            if (wantsBlazor)
            {
                typeScriptProjects.Add("blazor");
            }

            if (wantsReact && (TestEnvironment.HasValue("PLAYWRIGHT_REACT_URL") || TestEnvironment.HasValue("TASKFLOW_REACT_BASE_URL")))
            {
                var reactBaseUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_REACT_URL")
                    ?? Environment.GetEnvironmentVariable("TASKFLOW_REACT_BASE_URL");
                environment.Set("PLAYWRIGHT_REACT_URL", reactBaseUrl?.TrimEnd('/'));
                typeScriptProjects.Add("react");
            }
            else if (reactRunnable)
            {
                await ResolveEndpointAsync(app, hostContext, environment, ReactResourceName, "PLAYWRIGHT_REACT_URL", ct);
                typeScriptProjects.Add("react");
            }

            if (unoTarget.RunTypeScriptProject)
            {
                if (unoTarget.HostWithAspire)
                {
                    var unoBaseUrl = await WasmAppHost.ResolveEndpointAsync(hostContext, environment, app, ct);
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
                environment);
        }
        catch (DockerUnavailableException)
        {
            environment.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            var resourcesUnavailable = app is not null
                && ex is DistributedApplicationException
                && ResourcesFailedBeforeLaunch(app);

            if (app is not null)
            {
                await hostContext.DumpResourceDiagnosticsAsync(DiagnosticResourceNames, CancellationToken.None);

                try
                {
                    await hostContext.StopAndDisposeAsync(CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    Console.Error.WriteLine($"Aspire cleanup after startup failure also failed: {cleanupException.Message}");
                }
            }

            environment.Dispose();
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
            _environment.Dispose();
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

    internal Task DumpDiagnosticsAsync(CancellationToken cancellationToken) =>
        _hostContext.DumpResourceDiagnosticsAsync(DiagnosticResourceNames, cancellationToken);

    private static async Task<string> ResolveEndpointAsync(
        DistributedApplication app,
        AspireTestHostContext hostContext,
        EnvironmentVariableScope environment,
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
        environment.Set(environmentVariableName, endpoint);
        return endpoint;
    }

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
        var reactRoot = Path.Combine(RepositoryRoot.Find(), "src", "UI", "TaskFlow.React");
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
