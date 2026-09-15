using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using System.Diagnostics;
using Test.Support.Hosting;

namespace Test.Support.Aspire;

/// <summary>
/// Shared lifecycle policy for Aspire-backed tests: Docker preflight, one cumulative startup
/// deadline, named-resource waits with diagnostics, and bounded stop/dispose cleanup.
/// </summary>
public sealed class AspireTestHostContext
{
    private static readonly TimeSpan DockerPreflightLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResourceLogDiagnosticLimit = TimeSpan.FromSeconds(5);
    private readonly Stopwatch _startupClock = Stopwatch.StartNew();
    private readonly TimeSpan _startupBudget;
    private readonly TimeSpan _cleanupBudget;
    private readonly string _resourceLoggingEnvironmentVariable;
    private DistributedApplication? _app;

    public AspireTestHostContext(
        TimeSpan startupBudget,
        string resourceLoggingEnvironmentVariable,
        TimeSpan? cleanupBudget = null)
    {
        if (startupBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startupBudget), "Startup budget must be positive.");

        _startupBudget = startupBudget;
        _cleanupBudget = cleanupBudget ?? TimeSpan.FromMinutes(1);
        _resourceLoggingEnvironmentVariable = resourceLoggingEnvironmentVariable;
        ResourceLoggingEnabled = string.Equals(
            Environment.GetEnvironmentVariable(resourceLoggingEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    public bool ResourceLoggingEnabled { get; }

    public TimeSpan RemainingStartupBudget => GetRemainingStartupBudget("read remaining startup budget");

    public void Attach(DistributedApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (_app is not null)
            throw new InvalidOperationException("An Aspire application is already attached.");

        _app = app;
    }

    public async Task<string?> GetDockerUnavailableReasonAsync(CancellationToken cancellationToken)
    {
        var timeout = Min(DockerPreflightLimit, GetRemainingStartupBudget("Docker preflight"));
        return await DockerRuntimePreflight.GetUnavailableReasonAsync(timeout, cancellationToken);
    }

    public async Task RunStartupStepAsync(
        string stepName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await RunStartupStepAsync(
            stepName,
            async token =>
            {
                await operation(token);
                return true;
            },
            cancellationToken);
    }

    public async Task<T> RunStartupStepAsync<T>(
        string stepName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var remaining = GetRemainingStartupBudget(stepName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(remaining);

        try
        {
            return await operation(timeoutCts.Token).WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (
            !cancellationToken.IsCancellationRequested
            && timeoutCts.IsCancellationRequested)
        {
            throw CreateStartupTimeout(stepName, ex);
        }
    }

    public async Task WaitForResourceHealthyAsync(string resourceName, CancellationToken cancellationToken)
    {
        var app = _app ?? throw new InvalidOperationException("Aspire application is not attached.");
        try
        {
            await RunStartupStepAsync(
                $"wait for resource '{resourceName}'",
                token => app.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, token),
                cancellationToken);
        }
        catch
        {
            await DumpResourceDiagnosticsAsync(resourceName, CancellationToken.None);
            throw;
        }
    }

    public async Task DumpResourceDiagnosticsAsync(string resourceName, CancellationToken cancellationToken)
    {
        var app = _app;
        if (app is null)
        {
            Console.Error.WriteLine($"{resourceName}: Aspire application was not built.");
            return;
        }

        if (app.ResourceNotifications.TryGetCurrentState(resourceName, out var resourceEvent))
        {
            var snapshot = resourceEvent.Snapshot;
            Console.Error.WriteLine(
                $"{resourceName}: state={snapshot.State?.Text}; health={snapshot.HealthStatus}; "
                + $"exit={snapshot.ExitCode}; started={snapshot.StartTimeStamp:O}; stopped={snapshot.StopTimeStamp:O}");
        }
        else
        {
            Console.Error.WriteLine($"{resourceName}: no resource state available.");
        }

        if (!ResourceLoggingEnabled)
        {
            Console.Error.WriteLine(
                $"{resourceName}: resource logs disabled; set {_resourceLoggingEnvironmentVariable}=true to include them.");
            return;
        }

        var logs = app.Services.GetService(typeof(ResourceLoggerService)) as ResourceLoggerService;
        if (logs is null)
        {
            Console.Error.WriteLine($"{resourceName}: resource logger unavailable.");
            return;
        }

        using var logCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        logCts.CancelAfter(ResourceLogDiagnosticLimit);
        try
        {
            await foreach (var batch in logs.GetAllAsync(resourceName).WithCancellation(logCts.Token))
            {
                foreach (var line in batch)
                    Console.Error.WriteLine($"{resourceName}: {line}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"{resourceName}: resource logs unavailable: {ex.Message}");
        }
    }

    public async Task StopAndDisposeAsync(CancellationToken cancellationToken)
    {
        var app = _app;
        _app = null;
        if (app is null)
            return;

        var cleanupClock = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            await RunCleanupStepAsync(
                "stop Aspire application",
                token => app.StopAsync(token),
                cleanupClock,
                cancellationToken,
                _cleanupBudget / 2);
        }
        catch (Exception ex)
        {
            failure = ex;
            Console.Error.WriteLine($"Aspire cleanup stop failed: {ex.Message}");
        }

        try
        {
            await RunCleanupStepAsync(
                "dispose Aspire application",
                _ => app.DisposeAsync().AsTask(),
                cleanupClock,
                cancellationToken);
        }
        catch (Exception ex)
        {
            failure = failure is null ? ex : new AggregateException(failure, ex);
            Console.Error.WriteLine($"Aspire cleanup dispose failed: {ex.Message}");
        }

        if (failure is not null)
            throw new InvalidOperationException("Aspire cleanup did not complete within its bounded budget.", failure);
    }

    public static TimeSpan ReadPositiveSeconds(string variableName, int defaultSeconds)
    {
        var configured = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(configured))
            return TimeSpan.FromSeconds(defaultSeconds);

        return int.TryParse(configured, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException($"{variableName} must be a positive integer number of seconds.");
    }

    private async Task RunCleanupStepAsync(
        string stepName,
        Func<CancellationToken, Task> operation,
        Stopwatch cleanupClock,
        CancellationToken cancellationToken,
        TimeSpan? stepLimit = null)
    {
        var remaining = _cleanupBudget - cleanupClock.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException($"Cleanup budget {_cleanupBudget.TotalSeconds:0}s expired before {stepName}.");

        if (stepLimit is not null)
            remaining = Min(remaining, stepLimit.Value);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(remaining);
        try
        {
            await operation(timeoutCts.Token).WaitAsync(remaining, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Cleanup budget {_cleanupBudget.TotalSeconds:0}s expired during {stepName}.", ex);
        }
    }

    private TimeSpan GetRemainingStartupBudget(string stepName)
    {
        var remaining = _startupBudget - _startupClock.Elapsed;
        return remaining > TimeSpan.Zero
            ? remaining
            : throw CreateStartupTimeout(stepName, null);
    }

    private TimeoutException CreateStartupTimeout(string stepName, Exception? innerException) => new(
        $"Global Aspire startup budget {_startupBudget.TotalSeconds:0}s expired during {stepName}; "
        + $"elapsed={_startupClock.Elapsed.TotalSeconds:0}s.",
        innerException);

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
