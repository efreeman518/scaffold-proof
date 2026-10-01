using Aspire.Hosting;
using Aspire.Hosting.Testing;
using EF.IntegrationTesting.Aspire;
using EF.Testing.Environment;
using EF.Testing.Processes;
using System.ComponentModel;
using System.Text;
using TaskFlow.Uno.WasmHost;

namespace Test.PlaywrightUI.Hosting;

/// <summary>
/// Builds and exposes the TaskFlow Uno WASM host for Aspire-backed Playwright tests.
/// </summary>
internal static class WasmAppHost
{
    private const string TestsEnabledVariable = "TASKFLOW_WASM_TESTS_ENABLED";
    private const string UnoResourceName = "taskflowuno";
    private const string HttpEndpointName = "http";
    private const string TargetFramework = "net10.0-browserwasm";
    private const string StampFileName = ".taskflow-wasm-test-build.stamp";
    private const string PublishedDistPathVariable = "TASKFLOW_UNO_WASM_DIST_PATH";

    // Child dotnet builds run without an attached profiler (a null value removes the variable), node reuse, or telemetry.
    private static readonly Dictionary<string, string?> DotnetEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["COR_ENABLE_PROFILING"] = "0",
        ["COR_PROFILER"] = null,
        ["COR_PROFILER_PATH"] = null,
        ["COR_PROFILER_PATH_32"] = null,
        ["COR_PROFILER_PATH_64"] = null,
        ["CORECLR_ENABLE_PROFILING"] = "0",
        ["CORECLR_PROFILER"] = null,
        ["CORECLR_PROFILER_PATH"] = null,
        ["CORECLR_PROFILER_PATH_32"] = null,
        ["CORECLR_PROFILER_PATH_64"] = null,
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1"
    };

    internal static async Task<WasmHostTarget> PrepareAsync(
        AspireTestHostContext host,
        EnvironmentVariableScope environment,
        bool publishRelease,
        CancellationToken ct)
    {
        if (TestEnvironment.IsFalse(TestsEnabledVariable))
        {
            return new WasmHostTarget(
                RunTypeScriptProject: false,
                HostWithAspire: false,
                Message: $"Uno WASM disabled by {TestsEnabledVariable}.");
        }

        var configuredUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_UNO_URL");
        if (!publishRelease && !string.IsNullOrWhiteSpace(configuredUrl))
        {
            environment.Set("PLAYWRIGHT_UNO_URL", configuredUrl.TrimEnd('/'));
            return new WasmHostTarget(
                RunTypeScriptProject: true,
                HostWithAspire: false,
                Message: $"Uno WASM uses configured PLAYWRIGHT_UNO_URL={configuredUrl.TrimEnd('/')}.");
        }

        var repoRoot = RepositoryRoot.Find(markers: "TaskFlow.slnx");
        var unoProject = Path.Combine(repoRoot, "src", "UI", "TaskFlow.Uno", "TaskFlow.Uno.csproj");
        if (!File.Exists(unoProject))
        {
            throw new InvalidOperationException($"Uno project not found: {unoProject}");
        }

        var configuration = publishRelease ? "Release" : GetCurrentConfiguration();
        CleanTargetOutput(repoRoot, configuration);

        await RunDotnetAsync(
            host,
            "Uno WASM restore",
            repoRoot,
            [
                "restore",
                unoProject,
                "-p:BuildAllUnoTargets=true",
                "-p:EnableUnoWasm=true",
                $"-p:Configuration={configuration}"
            ],
            ct);

        var outputPath = Path.Combine(repoRoot, "src", "UI", "TaskFlow.Uno", "bin", configuration, TargetFramework);
        if (publishRelease)
        {
            outputPath = Path.Combine(outputPath, "publish");
            await RunDotnetAsync(
                host,
                "Uno WASM Release publish",
                repoRoot,
                [
                    "publish",
                    unoProject,
                    "-f",
                    TargetFramework,
                    $"-p:TargetFrameworkOverride={TargetFramework}",
                    "-p:EnableUnoWasm=true",
                    "-p:Configuration=Release",
                    $"-p:PublishDir={outputPath}",
                    "--no-restore",
                    "-m:1"
                ],
                ct);

            environment.Set(PublishedDistPathVariable, outputPath);
        }
        else
        {
            await RunDotnetAsync(
                host,
                "Uno WASM build",
                repoRoot,
                [
                    "build",
                    unoProject,
                    $"-p:TargetFrameworkOverride={TargetFramework}",
                    "-p:EnableUnoWasm=true",
                    $"-p:Configuration={configuration}",
                    "--no-restore",
                    "-m:1"
                ],
                ct);

            environment.Set(PublishedDistPathVariable, null);
        }

        _ = PublishedAssetContract.Validate(outputPath);

        File.WriteAllText(
            Path.Combine(outputPath, StampFileName),
            DateTimeOffset.UtcNow.ToString("O"),
            Encoding.UTF8);

        return new WasmHostTarget(
            RunTypeScriptProject: true,
            HostWithAspire: true,
            Message: publishRelease
                ? $"Uno WASM assets restored and published for Release/{TargetFramework} at {outputPath}."
                : $"Uno WASM assets restored and built for {configuration}/{TargetFramework}.");
    }

    internal static async Task<string> ResolveEndpointAsync(
        AspireTestHostContext host,
        EnvironmentVariableScope environment,
        DistributedApplication app,
        CancellationToken ct)
    {
        await host.WaitForResourceHealthyAsync(UnoResourceName, ct);

        var endpoint = app.GetEndpoint(UnoResourceName, HttpEndpointName).ToString().TrimEnd('/');
        environment.Set("PLAYWRIGHT_UNO_URL", endpoint);
        return endpoint;
    }

    private static string GetCurrentConfiguration()
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var configuration = outputDirectory.Parent?.Name;
        return string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration;
    }

    private static void CleanTargetOutput(string repoRoot, string configuration)
    {
        var unoRoot = Path.Combine(repoRoot, "src", "UI", "TaskFlow.Uno");
        DeleteDirectoryIfExists(Path.Combine(unoRoot, "bin", configuration, TargetFramework), unoRoot);
        DeleteDirectoryIfExists(Path.Combine(unoRoot, "obj", configuration, TargetFramework), unoRoot);
    }

    private static void DeleteDirectoryIfExists(string path, string allowedRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var fullAllowedRoot = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullPath.StartsWith(fullAllowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete outside Uno project root: {fullPath}");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static async Task RunDotnetAsync(
        AspireTestHostContext host,
        string stepName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        await host.RunStartupStepAsync(stepName, RunAsync, ct);
        return;

        async Task RunAsync(CancellationToken stepToken)
        {
            ProcessResult result;
            try
            {
                // The step token enforces the global startup deadline; the timeout is the same bound as a backstop.
                result = await ProcessRunner.RunAsync(
                    new ProcessRunOptions
                    {
                        FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        Environment = DotnetEnvironment,
                        Timeout = host.RemainingStartupBudget
                    },
                    stepToken);
            }
            catch (Win32Exception ex)
            {
                throw new WasmPrerequisiteException("dotnet CLI is not available on PATH.", ex.Message);
            }

            if (result.TimedOut)
            {
                throw new TimeoutException(
                    $"{stepName} exceeded the global startup deadline.{Environment.NewLine}{result.CombinedOutput}");
            }

            if (result.ExitCode == 0)
            {
                return;
            }

            if (LooksLikeMissingWasmWorkload(result.CombinedOutput))
            {
                throw new WasmPrerequisiteException(
                    "Uno WASM workload missing. Run: dotnet workload install wasm-tools",
                    result.CombinedOutput);
            }

            throw new InvalidOperationException(
                $"{stepName} failed with exit code {result.ExitCode}. Command: dotnet {FormatArguments(arguments)}"
                + Environment.NewLine
                + result.CombinedOutput);
        }
    }

    private static bool LooksLikeMissingWasmWorkload(string output) =>
        output.Contains("UNOWA0001", StringComparison.OrdinalIgnoreCase)
        || output.Contains("wasm-tools workload could not be located", StringComparison.OrdinalIgnoreCase);

    private static string FormatArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(argument =>
            argument.Contains(' ', StringComparison.Ordinal)
                ? $"\"{argument}\""
                : argument));
}

internal sealed record WasmHostTarget(
    bool RunTypeScriptProject,
    bool HostWithAspire,
    string Message);

internal sealed class WasmPrerequisiteException : Exception
{
    internal WasmPrerequisiteException(string message, string detail)
        : base(string.IsNullOrWhiteSpace(detail) ? message : $"{message}{Environment.NewLine}{detail}")
    {
    }
}
