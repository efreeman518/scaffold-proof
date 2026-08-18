using Aspire.Hosting;
using Aspire.Hosting.Testing;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TaskFlow.Uno.WasmHost;
using Test.Support.Aspire;

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

    private static readonly string[] ProfilerVariables =
    [
        "COR_ENABLE_PROFILING",
        "COR_PROFILER",
        "COR_PROFILER_PATH",
        "COR_PROFILER_PATH_32",
        "COR_PROFILER_PATH_64",
        "CORECLR_ENABLE_PROFILING",
        "CORECLR_PROFILER",
        "CORECLR_PROFILER_PATH",
        "CORECLR_PROFILER_PATH_32",
        "CORECLR_PROFILER_PATH_64"
    ];

    internal static async Task<WasmHostTarget> PrepareAsync(
        AspireTestHostContext host,
        bool publishRelease,
        CancellationToken ct)
    {
        if (IsExplicitlyDisabled(Environment.GetEnvironmentVariable(TestsEnabledVariable)))
        {
            return new WasmHostTarget(
                RunTypeScriptProject: false,
                HostWithAspire: false,
                Message: $"Uno WASM disabled by {TestsEnabledVariable}.");
        }

        var configuredUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_UNO_URL");
        if (!publishRelease && !string.IsNullOrWhiteSpace(configuredUrl))
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_UNO_URL", configuredUrl.TrimEnd('/'));
            return new WasmHostTarget(
                RunTypeScriptProject: true,
                HostWithAspire: false,
                Message: $"Uno WASM uses configured PLAYWRIGHT_UNO_URL={configuredUrl.TrimEnd('/')}.");
        }

        var repoRoot = FindRepoRoot()
            ?? throw new InvalidOperationException("Could not find repo root from Test.PlaywrightUI output path.");
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

            Environment.SetEnvironmentVariable(PublishedDistPathVariable, outputPath);
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

            Environment.SetEnvironmentVariable(PublishedDistPathVariable, null);
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
        DistributedApplication app,
        CancellationToken ct)
    {
        await host.WaitForResourceHealthyAsync(UnoResourceName, ct);

        var endpoint = app.GetEndpoint(UnoResourceName, HttpEndpointName).ToString().TrimEnd('/');
        Environment.SetEnvironmentVariable("PLAYWRIGHT_UNO_URL", endpoint);
        return endpoint;
    }

    private static bool IsExplicitlyDisabled(string? value) =>
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "TaskFlow.slnx"))
                || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
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
            var fileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            var startInfo = new ProcessStartInfo(fileName)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var variable in ProfilerVariables)
            {
                startInfo.Environment.Remove(variable);
            }

            startInfo.Environment["CORECLR_ENABLE_PROFILING"] = "0";
            startInfo.Environment["COR_ENABLE_PROFILING"] = "0";
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";

            using var process = StartProcess(startInfo, stepName);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(stepToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                var timedOutOutput = await ReadOutputAsync(stdout, stderr);
                Console.Error.WriteLine($"{stepName} cancelled by the global startup deadline.{Environment.NewLine}{timedOutOutput}");
                throw;
            }

            var output = await ReadOutputAsync(stdout, stderr);
            if (process.ExitCode == 0)
            {
                return;
            }

            if (LooksLikeMissingWasmWorkload(output))
            {
                throw new WasmPrerequisiteException(
                    "Uno WASM workload missing. Run: dotnet workload install wasm-tools",
                    output);
            }

            throw new InvalidOperationException(
                $"{stepName} failed with exit code {process.ExitCode}. Command: dotnet {FormatArguments(arguments)}"
                + Environment.NewLine
                + output);
        }
    }

    private static Process StartProcess(ProcessStartInfo startInfo, string stepName)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {stepName}.");
        }
        catch (Win32Exception ex)
        {
            throw new WasmPrerequisiteException("dotnet CLI is not available on PATH.", ex.Message);
        }
    }

    private static async Task<string> ReadOutputAsync(Task<string> stdout, Task<string> stderr)
    {
        var outputs = await Task.WhenAll(stdout, stderr);
        return string.Join(Environment.NewLine, outputs.Where(output => !string.IsNullOrWhiteSpace(output)));
    }

    private static bool LooksLikeMissingWasmWorkload(string output) =>
        output.Contains("UNOWA0001", StringComparison.OrdinalIgnoreCase)
        || output.Contains("wasm-tools workload could not be located", StringComparison.OrdinalIgnoreCase);

    private static string FormatArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(argument =>
            argument.Contains(' ', StringComparison.Ordinal)
                ? $"\"{argument}\""
                : argument));

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
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
