using System.Collections.Concurrent;
using TaskFlow.Scheduler.Telemetry;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Interfaces;

namespace TaskFlow.Scheduler.Infrastructure;

/// <summary>Handles task flow scheduler exception work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
public sealed class TaskFlowSchedulerExceptionHandler(
    ILogger<TaskFlowSchedulerExceptionHandler> logger,
    SchedulingMetrics metrics) : ITickerExceptionHandler
{
    private static readonly ConcurrentDictionary<Guid, string> JobNameCache = new();

    /// <summary>Registers job name dependencies in the service container.</summary>
    public static void RegisterJobName(Guid tickerId, string jobName)
    {
        JobNameCache.TryAdd(tickerId, jobName);
    }

    /// <summary>Provides the unregister job name operation for task flow scheduler exception handler.</summary>
    public static void UnregisterJobName(Guid tickerId)
    {
        JobNameCache.TryRemove(tickerId, out _);
    }

    /// <summary>Provides the handle exception operation for task flow scheduler exception handler.</summary>
    public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
    {
        var jobName = ResolveJobName(tickerId);
        logger.SchedulerJobFailed(exception, jobName, tickerId, tickerType);

        metrics.RecordJobFailure(jobName, exception.Message);
        UnregisterJobName(tickerId);
        return Task.CompletedTask;
    }

    /// <summary>Provides the handle canceled exception operation for task flow scheduler exception handler.</summary>
    public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
    {
        var jobName = ResolveJobName(tickerId);
        logger.SchedulerJobCancelled(jobName, tickerId, tickerType, exception.Message);

        UnregisterJobName(tickerId);
        return Task.CompletedTask;
    }

    /// <summary>Provides the resolve job name operation for task flow scheduler exception handler.</summary>
    private static string ResolveJobName(Guid tickerId) =>
        JobNameCache.TryGetValue(tickerId, out var jobName)
            ? jobName
            : $"Unknown-{tickerId:N}";
}
