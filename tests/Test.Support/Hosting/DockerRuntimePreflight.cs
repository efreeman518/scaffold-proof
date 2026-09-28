using System.ComponentModel;
using System.Diagnostics;

namespace Test.Support.Hosting;

/// <summary>
/// Performs one bounded Docker-compatible runtime capability check. Redirected stdout and stderr are
/// drained concurrently so a noisy CLI cannot deadlock the preflight.
/// Test prerequisite rule: a non-null reason (no usable runtime) is a missing optional prerequisite and reports
/// Inconclusive; once the preflight passes, a container or host that fails to start fails the test.
/// </summary>
public static class DockerRuntimePreflight
{
    /// <summary>The enabling step every unavailable reason ends with, so an Inconclusive result says how to run it.</summary>
    public const string EnablingStep =
        "Start a Docker-compatible runtime (Docker Desktop, Docker Engine, or Podman) until `docker info` succeeds.";

    public static async Task<string?> GetUnavailableReasonAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Docker preflight timeout must be positive.");

        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "docker.exe" : "docker", "info")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"Container runtime unavailable: {ex.Message}. {EnablingStep}";
        }

        if (process is null)
            return $"Container runtime unavailable: docker info did not start. {EnablingStep}";

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                var timedOutOutput = await ReadOutputAsync(stdoutTask, stderrTask);
                return $"Container runtime unavailable: docker info exceeded {timeout.TotalSeconds:0} seconds. {EnablingStep}"
                    + FormatOutput(timedOutOutput);
            }
            catch
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await ReadOutputAsync(stdoutTask, stderrTask);
                throw;
            }

            var output = await ReadOutputAsync(stdoutTask, stderrTask);
            return process.ExitCode == 0
                ? null
                : $"Container runtime unavailable: docker info exited {process.ExitCode}. {EnablingStep}"
                    + FormatOutput(output);
        }
    }

    private static async Task<string> ReadOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        var output = await Task.WhenAll(stdoutTask, stderrTask);
        return string.Join(Environment.NewLine, output.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatOutput(string output) => string.IsNullOrWhiteSpace(output)
        ? string.Empty
        : Environment.NewLine + (output.Length <= 4_000 ? output : output[..4_000] + "...");

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
