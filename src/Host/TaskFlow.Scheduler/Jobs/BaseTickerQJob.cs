using System.Diagnostics;
using TaskFlow.Scheduler.Abstractions;
using TaskFlow.Scheduler.Infrastructure;
using TaskFlow.Observability.Tracing;
using TaskFlow.Scheduler.Telemetry;
using TickerQ.Utilities.Base;

namespace TaskFlow.Scheduler.Jobs;

/// <summary>
/// Shared TickerQ job wrapper. It creates one DI scope per execution, records metrics, registers
/// the job name for exception handling, and rethrows failures so TickerQ can apply its retry policy.
/// </summary>
public abstract class BaseTickerQJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly SchedulingMetrics _metrics;

    /// <summary>Initializes base ticker q job with required dependencies and default state.</summary>
    protected BaseTickerQJob(IServiceScopeFactory scopeFactory, ILogger logger, SchedulingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Resolves and runs the scheduled handler inside an async scope. Handler implementations own
    /// business logic; this method owns scheduler integration, logging, and telemetry.
    /// </summary>
    protected async Task ExecuteJobAsync<THandler>(
        string jobName, TickerFunctionContext context, CancellationToken ct)
        where THandler : IScheduledJobHandler
    {
        var sw = Stopwatch.StartNew();
        TaskFlowSchedulerExceptionHandler.RegisterJobName(context.Id, jobName);
        _logger.JobStarting(jobName, DateTime.UtcNow);

        // D-053: one span per job execution, here rather than in each handler - a scheduled run has no
        // incoming request to inherit a trace from, so this is the root that everything the handler does
        // (database work, outbox staging) hangs off.
        using var activity = TaskFlowActivitySources.Scheduler.StartActivity(
            $"{jobName} execute", ActivityKind.Internal);
        activity?.SetTag("scheduler.job.name", jobName);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();
            await handler.HandleAsync(ct);

            sw.Stop();
            _metrics.RecordJobSuccess(jobName, sw.Elapsed.TotalMilliseconds);
            _logger.JobCompleted(jobName, sw.ElapsedMilliseconds);
            TaskFlowSchedulerExceptionHandler.UnregisterJobName(context.Id);
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetBaseException().Message);
            _logger.TickerQJobExecutionFailed(ex, jobName, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
