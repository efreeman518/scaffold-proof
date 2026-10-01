using EF.Testing.Http;
using EF.Testing.Processes;
using System.Diagnostics;
using System.Net.Http;

namespace Test.Mobile;

/// <summary>
/// Builds the default Android package and owns the local Appium server for one test-host process.
/// </summary>
internal static class MobileTestHost
{
    // A restore or Android build that has not finished by then is hung; ProcessRunner kills its tree on timeout.
    private static readonly TimeSpan DotnetTimeout = TimeSpan.FromMinutes(30);
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static Process? _appiumProcess;

    internal static async Task EnsureReadyAsync(MobileTestSettings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.AppPath) && !Directory.Exists(settings.AppPath))
        {
            if (settings.HasConfiguredAppPath)
            {
                throw new InvalidOperationException($"Configured mobile app package was not found: {settings.AppPath}");
            }

            await BuildDefaultAndroidPackageAsync(settings.RepoRoot, cancellationToken);
        }

        if (!File.Exists(settings.AppPath) && !Directory.Exists(settings.AppPath))
        {
            throw new InvalidOperationException($"Mobile app package was not produced: {settings.AppPath}");
        }

        if (await IsAppiumReadyAsync(settings.AppiumServerUri, cancellationToken) is null)
        {
            return;
        }

        if (!settings.AppiumServerUri.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Appium server is unavailable at {settings.AppiumServerUri}. Only a loopback Appium endpoint is started automatically.");
        }

        var appium = _appiumProcess = StartAppium(settings.AppiumServerUri);
        try
        {
            await HttpReadiness.WaitAsync(
                new Uri(settings.AppiumServerUri, "status"),
                new HttpReadinessOptions
                {
                    Timeout = TimeSpan.FromSeconds(45),
                    PollInterval = TimeSpan.FromMilliseconds(250),
                    RequestTimeout = TimeSpan.FromSeconds(2)
                },
                cancellationToken);
        }
        catch (TimeoutException ex) when (appium.HasExited)
        {
            // The status endpoint never answered because Appium itself died: report the exit, not the wait.
            throw new InvalidOperationException($"Appium exited during startup with exit code {appium.ExitCode}.", ex);
        }
    }

    internal static async Task StopAsync()
    {
        var process = Interlocked.Exchange(ref _appiumProcess, null);
        if (process is null)
        {
            return;
        }

        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task BuildDefaultAndroidPackageAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var projectPath = Path.Combine(repoRoot, "src", "UI", "TaskFlow.Uno", "TaskFlow.Uno.csproj");
        await RunDotnetAsync(
            projectPath,
            repoRoot,
            ["restore", projectPath, "-p:BuildAllUnoTargets=true", "-p:Configuration=Debug"],
            cancellationToken);
        await RunDotnetAsync(
            projectPath,
            repoRoot,
            ["build", projectPath, "-p:TargetFrameworkOverride=net10.0-android", "-p:Configuration=Debug", "--no-restore", "-m:1"],
            cancellationToken);
    }

    private static Process StartAppium(Uri serverUri)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe")
            {
                // Required by MobileTaskCrudTests for reliable native text entry. This process only starts for loopback endpoints.
                Arguments = $"/d /s /c appium --address {serverUri.Host} --port {serverUri.Port} --base-path / --allow-insecure uiautomator2:adb_shell",
                CreateNoWindow = true,
                UseShellExecute = false
            }
        };
        process.Start();
        return process;
    }

    private static async Task<string?> IsAppiumReadyAsync(Uri serverUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync(new Uri(serverUri, "status"), cancellationToken);
            return response.IsSuccessStatusCode ? null : $"Appium status endpoint returned {(int)response.StatusCode}.";
        }
        catch (HttpRequestException ex)
        {
            return ex.Message;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ex.Message;
        }
    }

    private static async Task RunDotnetAsync(
        string projectPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // On timeout or cancellation (for example an MSTest cooperative timeout) the whole process tree is killed:
        // an orphaned dotnet restore/build holds obj/ locks that fail the next run.
        var result = await ProcessRunner.RunAsync(
            new ProcessRunOptions
            {
                FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Environment = new Dictionary<string, string?>
                {
                    ["MSBUILDDISABLENODEREUSE"] = "1",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["DOTNET_NOLOGO"] = "1"
                },
                Timeout = DotnetTimeout
            },
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                (result.TimedOut
                    ? $"dotnet {string.Join(' ', arguments)} for {projectPath} exceeded {DotnetTimeout.TotalMinutes:0} minutes."
                    : $"dotnet {string.Join(' ', arguments)} for {projectPath} failed with exit code {result.ExitCode}.")
                + Environment.NewLine
                + result.CombinedOutput);
        }
    }
}
