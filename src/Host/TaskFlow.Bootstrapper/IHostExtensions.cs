using EF.BackgroundServices.InternalMessageBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskFlow.Application.MessageHandlers;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Host lifecycle helpers shared by API, Functions, Scheduler, and tests.
/// </summary>
public static class IHostExtensions
{
    /// <summary>
    /// Registers every message handler in the application handler assembly with the singleton internal message
    /// bus by type: each dispatch resolves the handler from its own DI scope, so a scoped dependency (the audit
    /// sink's DbContext) lives exactly as long as that dispatch. A discovered handler missing from DI fails here.
    /// </summary>
    public static void AutoRegisterMessageHandlers(this IHost host) =>
        host.Services.GetRequiredService<IInternalMessageBus>().AutoRegisterHandlers(typeof(AuditHandler).Assembly);

    /// <summary>
    /// Registers message handlers before running startup tasks so migration, warmup, and
    /// later request processing share the same internal event pipeline.
    /// </summary>
    public static async Task RunStartupTasks(this IHost host)
    {
        host.AutoRegisterMessageHandlers();

        using var scope = host.Services.CreateScope();
        foreach (var task in scope.ServiceProvider.GetServices<IStartupTask>())
            await task.ExecuteAsync();
    }
}
