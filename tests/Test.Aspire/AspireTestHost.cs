using AppHost;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using EF.IntegrationTesting.Aspire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskFlow.Hosting;
using Test.Support.Aspire;
using Test.Support.Hosting;
using EnvironmentVariableScope = EF.Testing.Environment.EnvironmentVariableScope;
using FunctionsCoreToolsDiscovery = EF.Testing.Environment.FunctionsCoreToolsDiscovery;
using AspireTestHostContext = Test.Support.Aspire.AspireTestHostContext;

namespace Test.Aspire;

/// <summary>
/// Lazy assembly-scoped fixture that starts the Aspire AppHost graph for the selected D-060 lane (NonAzure
/// unless TASKFLOW_LANE=Azure) the first time a mesh test class calls <see cref="EnsureStartedAsync"/> from
/// <c>[ClassInitialize]</c>. Mesh tier (Aspire.Hosting.Testing) - the only tier that exercises the full
/// service mesh (HTTP -> API -> Service Bus -> Function -> projection -> audit row), which no lighter tier
/// reproduces. Teardown runs once via <c>AspireMeshLifecycle.[AssemblyCleanup]</c>. The shared
/// <see cref="AspireTestHostContext"/> owns Docker preflight, one cumulative startup deadline,
/// named resource waits, failure diagnostics, and bounded cleanup.
/// </summary>
internal static class AspireTestHost
{
    internal const string ResourceLoggingEnvironmentVariable = "TASKFLOW_ASPIRE_RESOURCE_LOGGING";
    internal const string RunAspireTestsEnvironmentVariable = "TASKFLOW_RUN_ASPIRE_TESTS";
    internal const string RunAzureFoundryTestsEnvironmentVariable = "TASKFLOW_RUN_AZURE_FOUNDRY_TESTS";
    internal const string RunFunctionsTestsEnvironmentVariable = "TASKFLOW_RUN_FUNCTIONS_TESTS";
    private const string StartupTimeoutEnvironmentVariable = "TASKFLOW_ASPIRE_STARTUP_TIMEOUT_SECONDS";

    /// <summary>Guards the lazy single-start so concurrent <c>[ClassInitialize]</c> calls boot the graph once.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static EnvironmentVariableScope? _environment;
    private static AspireTestHostContext? _hostContext;

    /// <summary>
    /// Set once the graph failed to start after the Docker preflight passed. Later mesh classes fail on it at once
    /// instead of paying another full startup budget; a startup failure is never Inconclusive.
    /// </summary>
    private static string? _startupFailure;
    internal static string ConnectionString = null!;

    internal static TimeSpan DefaultTimeout =>
        _hostContext?.RemainingStartupBudget
        ?? AspireTestHostContext.ReadPositiveSeconds(StartupTimeoutEnvironmentVariable, 900);

    /// <summary>Shared Aspire app started once for all Aspire-based mesh tests.</summary>
    internal static DistributedApplication? AspireApp { get; private set; }

    /// <summary>AI provider the shared Aspire graph exposed through AppHost configuration.</summary>
    internal static AspireAiProvider AiProvider { get; private set; } = AspireAiProvider.None;

    /// <summary>True when the React Vite project can run from this checkout.</summary>
    internal static bool ReactAvailable { get; private set; }

    /// <summary>True when built Uno WASM assets are present for the static host.</summary>
    internal static bool UnoWasmAvailable { get; private set; }

    /// <summary>True only when the internal Aspire diagnostic logging switch is enabled.</summary>
    internal static bool ResourceLoggingEnabled { get; private set; }

    /// <summary>
    /// Starts the Aspire graph on first call and returns immediately on subsequent calls. Mesh test classes
    /// call this from <c>[ClassInitialize]</c> so the ~60-90 s graph boot is paid only when a mesh test runs.
    /// </summary>
    internal static async Task EnsureStartedAsync(TestContext context)
    {
        if (_startupFailure is not null)
        {
            Assert.Fail(_startupFailure);
            return;
        }

        if (AspireApp is not null)
            return;

        if (string.Equals(
                Environment.GetEnvironmentVariable(RunAspireTestsEnvironmentVariable),
                "false",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"{RunAspireTestsEnvironmentVariable}=false - Aspire mesh tier opted out.");
            return;
        }

        await Gate.WaitAsync(context.CancellationToken);
        try
        {
            if (_startupFailure is not null)
            {
                Assert.Fail(_startupFailure);
                return;
            }

            if (AspireApp is not null)
                return;

            _hostContext = new AspireTestHostContext(
                AspireTestHostContext.ReadPositiveSeconds(StartupTimeoutEnvironmentVariable, 900),
                ResourceLoggingEnvironmentVariable);
            var dockerUnavailableReason = await _hostContext.GetDockerUnavailableReasonAsync(context.CancellationToken);
            if (dockerUnavailableReason is not null)
            {
                _hostContext = null;
                ReportMissingPrerequisite(RunAspireTestsEnvironmentVariable, dockerUnavailableReason);
                return;
            }

            try
            {
                await StartAsync(context.CancellationToken);
            }
            catch (Exception ex)
            {
                foreach (var resourceName in new[]
                {
                    "taskflowdb",
                    "taskflowmigrator",
                    "taskflowapi",
                    "taskflowgateway",
                    "taskflowfunctions",
                    "TableStorage1"
                })
                {
                    await _hostContext.DumpResourceDiagnosticsAsync(resourceName, CancellationToken.None);
                }

                try
                {
                    await StopAsync(CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    Console.Error.WriteLine($"Aspire cleanup after startup failure also failed: {cleanupException.Message}");
                }

                // Docker passed its preflight, so a graph that does not come up (timeout included) is a failed
                // start, not a missing prerequisite.
                _startupFailure = $"Aspire mesh graph failed to start after Docker preflight succeeded: {ex.Message}";
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }

    }

    private static async Task StartAsync(CancellationToken ct)
    {
        var hostContext = _hostContext ?? throw new InvalidOperationException("Aspire host context is not initialized.");
        // AppHost.cs reads these via Environment.GetEnvironmentVariable, so they must be process env vars.
        _environment = new EnvironmentVariableScope()
            .Set("TASKFLOW_ASPIRE_TESTING", "true");

        if (!IsExplicitlyDisabled(RunFunctionsTestsEnvironmentVariable) && EnsureFuncToolAvailable())
            _environment.Set("TASKFLOW_ASPIRE_FUNCTIONS_AVAILABLE", "true");

        ReactAvailable = !IsExplicitlyDisabled("TASKFLOW_REACT_TESTS_ENABLED") && IsReactRunnable();
        if (ReactAvailable)
            _environment.Set("TASKFLOW_ASPIRE_REACT_AVAILABLE", "true");

        var unoWasmDistPath = IsExplicitlyDisabled("TASKFLOW_WASM_TESTS_ENABLED")
            ? null
            : FindUnoWasmDistPath();
        UnoWasmAvailable = unoWasmDistPath is not null;
        if (UnoWasmAvailable)
        {
            _environment.Set("TASKFLOW_ASPIRE_UNO_WASM_AVAILABLE", "true");
            _environment.Set("TASKFLOW_UNO_WASM_DIST_PATH", unoWasmDistPath);
        }

        ResourceLoggingEnabled = hostContext.ResourceLoggingEnabled;
        var appHostProgramType = Type.GetType("Program, AppHost", throwOnError: true)!;

        var builder = await hostContext.RunStartupStepAsync(
            "create Aspire mesh test host",
            token => DistributedApplicationTestingBuilder.CreateAsync(
                appHostProgramType,
                args: [],
                configureBuilder: (appOptions, hostSettings) =>
                {
                    appOptions.DisableDashboard = true;
                    appOptions.EnableResourceLogging = ResourceLoggingEnabled;
                    hostSettings.Configuration ??= new();
                    hostSettings.Configuration["Parameters:sql-password"] = LocalSqlSettings.SharedSaPassword;
                },
                cancellationToken: token),
            ct);

        // Surface app-level diagnostics at Information while filtering out the noisy framework categories
        // (AspNetCore request logs, Aspire DCP/orchestration chatter). Drop the filters when debugging startup.
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            logging.AddFilter("Aspire.", LogLevel.Warning);
        });

        AspireApp = await hostContext.RunStartupStepAsync(
            "build Aspire mesh test host",
            token => builder.BuildAsync(token),
            ct);
        hostContext.Attach(AspireApp);
        await hostContext.RunStartupStepAsync(
            "start Aspire mesh test host",
            token => AspireApp.StartAsync(token),
            ct);
        AiProvider = SelectRequestedAiProviderForTesting();

        // Bounded by whatever remains of the ~900 s (15 min) cumulative startup budget (StartupTimeoutEnvironmentVariable,
        // default 900) - far more than the audit tests' own 2-minute windows below, so this wait was never the
        // bottleneck. The 2026-09 CI failures traced to ApiAuditPipelineTests.cs querying Table Storage by the
        // bare tenant id instead of AuditLogRepository's "{tenantId}|{yyyyMMdd}" partition key, not to SQL
        // Server startup timing - the pre-login handshake lines seen in diagnostics were unrelated health-check
        // probing dumped at failure time, not the actual cause.
        await hostContext.WaitForResourceHealthyAsync("taskflowdb", ct);

        ConnectionString = await hostContext.RunStartupStepAsync(
            "resolve taskflowdb connection string",
            token => AspireApp.GetRequiredConnectionStringAsync(
                "taskflowdb",
                hostContext.RemainingStartupBudget,
                token),
            ct);
    }

    /// <summary>
    /// Stops and disposes the Aspire graph (if it was started) and restores mutated env vars. Invoked once
    /// by <c>AspireMeshLifecycle.[AssemblyCleanup]</c>, regardless of which mesh class warmed the graph up.
    /// </summary>
    internal static async Task StopAsync(CancellationToken ct)
    {
        var hostContext = _hostContext;
        try
        {
            if (hostContext is not null)
                await hostContext.StopAndDisposeAsync(ct);
        }
        finally
        {
            AspireApp = null;
            _hostContext = null;
            _environment?.Dispose();
            _environment = null;
            AiProvider = AspireAiProvider.None;
            ReactAvailable = false;
            UnoWasmAvailable = false;
            ResourceLoggingEnabled = false;
        }
    }

    /// <summary>
    /// Waits for a named resource within the one cumulative startup budget. A resource in the started graph that
    /// never turns healthy failed to start, so the timeout fails the test (diagnostics are dumped first).
    /// </summary>
    internal static Task WaitForResourceHealthyAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        var hostContext = _hostContext ?? throw new InvalidOperationException("Aspire host context is not initialized.");
        return hostContext.WaitForResourceHealthyAsync(resourceName, cancellationToken);
    }

    /// <summary>
    /// Functions Core Tools are an optional prerequisite: opted out, or not installed on a default run, reports
    /// Inconclusive with the opt-out variable and the install command; not installed with
    /// <c>TASKFLOW_RUN_FUNCTIONS_TESTS=true</c> fails. Once <c>func</c> is present the graph includes the Functions
    /// host, and a host that does not become healthy fails the test.
    /// </summary>
    internal static async Task RequireFunctionsHostAsync(CancellationToken cancellationToken)
    {
        if (IsExplicitlyDisabled(RunFunctionsTestsEnvironmentVariable))
        {
            Assert.Inconclusive($"{RunFunctionsTestsEnvironmentVariable}=false - Functions full-stack tests opted out.");
            return;
        }

        if (!EnsureFuncToolAvailable())
        {
            ReportMissingPrerequisite(
                RunFunctionsTestsEnvironmentVariable,
                "Azure Functions Core Tools ('func') not found. Install them with "
                + "`npm install -g azure-functions-core-tools@4`, or set "
                + $"{RunFunctionsTestsEnvironmentVariable}=false to opt out.");
            return;
        }

        await WaitForResourceHealthyAsync("taskflowfunctions", cancellationToken);
    }

    internal static Task RunStartupStepAsync(
        string stepName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var hostContext = _hostContext ?? throw new InvalidOperationException("Aspire host context is not initialized.");
        return hostContext.RunStartupStepAsync(stepName, operation, cancellationToken);
    }

    internal static async Task DumpResourceDiagnosticsAsync(
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        var hostContext = _hostContext;
        if (hostContext is not null)
            await hostContext.DumpResourceDiagnosticsAsync(resourceName, cancellationToken);
    }

    /// <summary>
    /// Checks if Azure Functions Core Tools (func.exe) is available on PATH.
    /// Mutates PATH to include the discovery location on Windows if found via LocalAppData fallback.
    /// </summary>
    internal static bool EnsureFuncToolAvailable() => FunctionsCoreToolsDiscovery.EnsureFuncToolAvailable();

    /// <summary>
    /// D-060: NonAzure is the unset default, so a mesh class that only exists on one lane (the Table audit
    /// sink, the Functions host) reports inconclusive on the other instead of timing out on a missing resource.
    /// </summary>
    internal static void RequireLaneOrInconclusive(HostingLane lane)
    {
        var current = TestHostingLane.Current.Lane;
        if (current != lane)
            Assert.Inconclusive($"Requires {HostingLaneResolver.LaneEnvironmentVariable}={lane}; current lane is {current}.");
    }

    internal static void RequireAzureFoundryOrInconclusive()
    {
        if (IsExplicitlyDisabled(RunAzureFoundryTestsEnvironmentVariable))
        {
            Assert.Inconclusive(
                $"{RunAzureFoundryTestsEnvironmentVariable}=false - Azure Foundry live tests opted out.");
        }

        var provider = AspireApp is null ? SelectRequestedAiProviderForTesting() : AiProvider;
        if (provider != AspireAiProvider.AzureFoundry)
        {
            Assert.Inconclusive(
                $"Azure AI Foundry is not selected. Set {HostingLaneResolver.LaneEnvironmentVariable}=Azure and "
                + $"{HostingLaneResolver.AiEnvironmentVariable}=AzureInference with AiServices:FoundryEndpoint configured, "
                + $"or set {RunAzureFoundryTestsEnvironmentVariable}=false to opt out.");
        }
    }

    internal static AspireAiProvider SelectRequestedAiProviderForTesting()
    {
        return IsAzureFoundryRequested() ? AspireAiProvider.AzureFoundry : AspireAiProvider.None;
    }

    /// <summary>
    /// Live Foundry runs exactly when the AppHost wires Foundry: when the shared lane resolver selects
    /// AzureInference. Foundry settings under any other provider make the AppHost fail at build, not select Foundry.
    /// </summary>
    private static bool IsAzureFoundryRequested() =>
        string.Equals(HostingLaneResolver.ResolveFromEnvironment().AiServices, "AzureInference", StringComparison.Ordinal);

    /// <summary>
    /// True when an opt-out variable is <c>false</c>, <c>0</c> or <c>no</c>. The one check for every
    /// <c>TASKFLOW_*_TESTS_ENABLED</c> switch, so the host skipping a surface and the test explaining the
    /// skip can never disagree about what counts as an opt-out.
    /// </summary>
    internal static bool IsExplicitlyDisabled(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a lane switch is <c>true</c>, <c>1</c> or <c>yes</c>: the operator asked for that lane.</summary>
    internal static bool IsExplicitlyEnabled(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Test prerequisite rule for a missing optional prerequisite: Inconclusive on a default run, with the enabling
    /// command in <paramref name="message"/>; a failure when the operator explicitly enabled the lane through
    /// <paramref name="laneVariable"/>, because an enabled lane that cannot run is not a skip.
    /// </summary>
    internal static void ReportMissingPrerequisite(string laneVariable, string message)
    {
        if (IsExplicitlyEnabled(laneVariable))
            Assert.Fail($"{laneVariable} is explicitly enabled, but a prerequisite is missing. {message}");

        Assert.Inconclusive(message);
    }

    private static bool IsReactRunnable()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
            return false;

        var reactRoot = Path.Combine(repoRoot, "src", "UI", "TaskFlow.React");
        var viteShim = OperatingSystem.IsWindows()
            ? Path.Combine(reactRoot, "node_modules", ".bin", "vite.cmd")
            : Path.Combine(reactRoot, "node_modules", ".bin", "vite");

        return File.Exists(Path.Combine(reactRoot, "package.json"))
            && File.Exists(viteShim);
    }

    private static string? FindUnoWasmDistPath()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
            return null;

        var outputRoot = Path.Combine(repoRoot, "src", "UI", "TaskFlow.Uno", "bin");
        if (!Directory.Exists(outputRoot))
            return null;

        return Directory.EnumerateFiles(outputRoot, "index.html", SearchOption.AllDirectories)
            .Where(path => path.Contains("net10.0-browserwasm", StringComparison.OrdinalIgnoreCase)
                && path.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(path => Directory.GetParent(Path.GetDirectoryName(path)!)?.FullName)
            .FirstOrDefault(path => path is not null);
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TaskFlow.slnx")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        return null;
    }

    internal sealed record AiStatus(string Provider, bool IsConfigured);
}

/// <summary>Describes the model provider wired into the Aspire test graph.</summary>
internal enum AspireAiProvider
{
    None,
    AzureFoundry
}
