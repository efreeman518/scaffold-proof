using EF.Testing.Environment;
using EF.Testing.Processes;
using System.ComponentModel;
using System.Text.Json;

namespace Test.PlaywrightUI;

/// <summary>
/// Runs the existing TypeScript Playwright suite from MSTest after Aspire has selected runnable hosts.
/// </summary>
internal static class TypeScriptPlaywrightRunner
{
    private const string ProjectTimeoutVariable = "TASKFLOW_PLAYWRIGHT_PROJECT_TIMEOUT_SECONDS";
    private const string TestTimeoutVariable = "TASKFLOW_PLAYWRIGHT_TEST_TIMEOUT_SECONDS";
    private const string UseSystemChromeVariable = "PLAYWRIGHT_USE_SYSTEM_CHROME";

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
                Environment.GetEnvironmentVariable(UseSystemChromeVariable),
                "false",
                StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserReadiness(true,
                $"Playwright-managed Chromium is missing; using installed Chrome: {systemChrome}",
                UseSystemChrome: true);
        }

        return new BrowserReadiness(false,
            "Playwright Chromium is missing and no system Chrome fallback is available. Run `npx --prefix tests/Test.PlaywrightUI playwright install chromium`, or set TASKFLOW_PLAYWRIGHT_TESTS_ENABLED=false to opt out.");
    }

    internal static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> projects,
        BrowserReadiness readiness,
        CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
        {
            return new ProcessResult(0, "No TypeScript Playwright projects selected.", "", TimedOut: false, TimeSpan.Zero);
        }

        var stdoutBuilder = new System.Text.StringBuilder();
        var stderrBuilder = new System.Text.StringBuilder();
        var elapsed = TimeSpan.Zero;

        try
        {
            foreach (var project in projects)
            {
                var result = await RunProjectAsync(project, readiness.UseSystemChrome, cancellationToken);
                elapsed += result.Elapsed;
                stdoutBuilder.AppendLine($"== {project} stdout ==");
                stdoutBuilder.AppendLine(result.StandardOutput);
                stderrBuilder.AppendLine($"== {project} stderr ==");
                stderrBuilder.AppendLine(result.StandardError);

                if (!result.Succeeded)
                {
                    return new ProcessResult(
                        result.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString(), result.TimedOut, elapsed);
                }
            }

            return new ProcessResult(0, stdoutBuilder.ToString(), stderrBuilder.ToString(), TimedOut: false, elapsed);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Node.js is not available on PATH.", ex);
        }
    }

    private static Task<ProcessResult> RunProjectAsync(
        string project,
        bool useSystemChrome,
        CancellationToken cancellationToken)
    {
        var isUno = project.StartsWith("uno", StringComparison.OrdinalIgnoreCase);
        var testTimeout = TestEnvironment.GetPositiveSeconds(TestTimeoutVariable, TimeSpan.FromSeconds(isUno ? 180 : 90));

        // A project that exceeds its timeout is killed with its process tree and reported as TimedOut; caller
        // cancellation kills it the same way and propagates.
        return ProcessRunner.RunAsync(
            new ProcessRunOptions
            {
                FileName = OperatingSystem.IsWindows() ? "node.exe" : "node",
                Arguments =
                [
                    PlaywrightCliPath,
                    "test",
                    $"--project={project}",
                    "--retries=0",
                    "--max-failures=1",
                    $"--timeout={(long)testTimeout.TotalMilliseconds}"
                ],
                WorkingDirectory = ProjectDirectory,
                // The Chrome fallback decision is handed to this Playwright child only, never set process-wide.
                Environment = useSystemChrome
                    ? new Dictionary<string, string?> { [UseSystemChromeVariable] = "true" }
                    : new Dictionary<string, string?>(),
                Timeout = TestEnvironment.GetPositiveSeconds(ProjectTimeoutVariable, TimeSpan.FromSeconds(isUno ? 360 : 180))
            },
            cancellationToken);
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

internal sealed record BrowserReadiness(bool CanRun, string Message, bool UseSystemChrome = false);
