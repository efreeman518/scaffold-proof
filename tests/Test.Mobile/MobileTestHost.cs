using System.Diagnostics;
using System.Net.Http;

namespace Test.Mobile;

/// <summary>
/// Builds the default Android package and owns the local Appium server for one test-host process.
/// </summary>
internal static class MobileTestHost
{
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

        _appiumProcess = StartAppium(settings.AppiumServerUri);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastError = await IsAppiumReadyAsync(settings.AppiumServerUri, cancellationToken);
            if (lastError is null)
            {
                return;
            }

            if (_appiumProcess.HasExited)
            {
                throw new InvalidOperationException(
                    $"Appium exited during startup with exit code {_appiumProcess.ExitCode}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException($"Appium did not become ready at {settings.AppiumServerUri}: {lastError}");
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
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
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

        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start dotnet for {projectPath}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}"
                + await ReadOutputAsync(stdout, stderr));
        }
    }

    private static async Task<string> ReadOutputAsync(Task<string> stdout, Task<string> stderr) =>
        string.Join(Environment.NewLine, (await Task.WhenAll(stdout, stderr)).Where(output => !string.IsNullOrWhiteSpace(output)));
}
