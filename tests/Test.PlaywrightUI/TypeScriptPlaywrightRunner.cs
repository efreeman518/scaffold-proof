using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Test.PlaywrightUI;

/// <summary>
/// Runs the existing TypeScript Playwright suite from MSTest after Aspire has selected runnable hosts.
/// </summary>
internal static class TypeScriptPlaywrightRunner
{
    private const string ProjectTimeoutVariable = "TASKFLOW_PLAYWRIGHT_PROJECT_TIMEOUT_SECONDS";
    private const string TestTimeoutVariable = "TASKFLOW_PLAYWRIGHT_TEST_TIMEOUT_SECONDS";

    internal static string ProjectDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", ".."));

    private static string PlaywrightCliPath =>
        Path.Combine(ProjectDirectory, "node_modules", "@playwright", "test", "cli.js");

    internal static BrowserReadiness CheckReadiness()
    {
        if (!File.Exists(PlaywrightCliPath))
        {
            return new BrowserReadiness(false,
                "TypeScript Playwright dependencies are missing. Run `npm ci --prefix tests/Test.PlaywrightUI`, or set TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false to opt out.");
        }

        var managedBrowser = GetManagedHeadlessShellPath();
        if (managedBrowser is not null && File.Exists(managedBrowser))
        {
            return new BrowserReadiness(true, $"Using Playwright-managed Chromium: {managedBrowser}");
        }

        var systemChrome = GetSystemChromePath();
        if (systemChrome is not null && !string.Equals(
                Environment.GetEnvironmentVariable("PLAYWRIGHT_USE_SYSTEM_CHROME"),
                "false",
                StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_USE_SYSTEM_CHROME", "true");
            return new BrowserReadiness(true,
                $"Playwright-managed Chromium is missing; using installed Chrome: {systemChrome}");
        }

        return new BrowserReadiness(false,
            "Playwright Chromium is missing and no system Chrome fallback is available. Run `npx --prefix tests/Test.PlaywrightUI playwright install chromium`, or set TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false to opt out.");
    }

    internal static async Task<CommandResult> RunAsync(
        IReadOnlyList<string> projects,
        CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
        {
            return new CommandResult(0, "No TypeScript Playwright projects selected.", "");
        }

        var stdoutBuilder = new System.Text.StringBuilder();
        var stderrBuilder = new System.Text.StringBuilder();

        try
        {
            foreach (var project in projects)
            {
                var result = await RunProjectAsync(project, cancellationToken);
                stdoutBuilder.AppendLine($"== {project} stdout ==");
                stdoutBuilder.AppendLine(result.StandardOutput);
                stderrBuilder.AppendLine($"== {project} stderr ==");
                stderrBuilder.AppendLine(result.StandardError);

                if (result.ExitCode != 0)
                {
                    return new CommandResult(result.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
                }
            }

            return new CommandResult(0, stdoutBuilder.ToString(), stderrBuilder.ToString());
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Node.js is not available on PATH.", ex);
        }
    }

    private static async Task<CommandResult> RunProjectAsync(string project, CancellationToken cancellationToken)
    {
        var fileName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var startInfo = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = ProjectDirectory
        };

        startInfo.ArgumentList.Add(PlaywrightCliPath);
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add($"--project={project}");
        startInfo.ArgumentList.Add("--retries=0");
        startInfo.ArgumentList.Add("--max-failures=1");
        var isUno = project.StartsWith("uno", StringComparison.OrdinalIgnoreCase);
        startInfo.ArgumentList.Add($"--timeout={ReadSeconds(TestTimeoutVariable, isUno ? 180 : 90) * 1000}");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Playwright project {project}.");

        // Drain both pipes independently of the deadline so timeout diagnostics are not cancelled
        // with the process wait and a noisy child cannot block on a full redirected stream.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ReadSeconds(ProjectTimeoutVariable, isUno ? 360 : 180)));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            return new CommandResult(124, await stdout, await stderr);
        }

        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }

    private static int ReadSeconds(string variableName, int defaultSeconds)
    {
        var configured = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultSeconds;
        }

        return int.TryParse(configured, out var seconds) && seconds > 0
            ? seconds
            : throw new InvalidOperationException($"{variableName} must be a positive integer number of seconds.");
    }

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

    private static string? GetManagedHeadlessShellPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var browsersJsonPath = Path.Combine(ProjectDirectory, "node_modules", "playwright-core", "browsers.json");
        if (!File.Exists(browsersJsonPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(browsersJsonPath));
        var revision = document.RootElement.GetProperty("browsers")
            .EnumerateArray()
            .FirstOrDefault(browser =>
                browser.GetProperty("name").GetString() == "chromium-headless-shell")
            .GetProperty("revision")
            .GetString();

        if (string.IsNullOrWhiteSpace(revision))
        {
            return null;
        }

        var browserRoot = GetBrowserRoot();
        return Path.Combine(
            browserRoot,
            $"chromium_headless_shell-{revision}",
            "chrome-headless-shell-win64",
            "chrome-headless-shell.exe");
    }

    private static string GetBrowserRoot()
    {
        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (configured == "0")
        {
            return Path.Combine(ProjectDirectory, "node_modules", "playwright-core", ".local-browsers");
        }

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
            : configured;
    }

    private static string? GetSystemChromePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe")
        };

        return paths.FirstOrDefault(File.Exists);
    }
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed record BrowserReadiness(bool CanRun, string Message);
