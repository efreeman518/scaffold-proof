using EF.BackgroundServices;
using EF.BackgroundServices.InternalMessageBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Bootstrapper.StartupTasks;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.Caching;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Shared composition root used by every host. Keeps API, Functions, Scheduler, and tests on the
/// same service graph while each host decides which pipeline or trigger surface it exposes.
/// </summary>
public static partial class RegisterServices
{
    /// <summary>
    /// Registers cross-cutting infrastructure first: request context, persistence, cache,
    /// Azure service adapters, health checks, and startup tasks. Several adapters fall back
    /// to no-op implementations when local or cloud resources are absent.
    /// </summary>
    public static IServiceCollection RegisterInfrastructureServices(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSupportServices();

        AddRequestContext(services);
        AddDatabaseServices(services, config);
        services.AddTaskFlowCaching(config);
        AddAuditServices(services, config);
        AddStorageServices(services, config);
        AddMessagingServices(services, config);
        AddReadModelServices(services, config);
        AddHealthChecks(services, config);
        AddStartupTasks(services);

        return services;
    }

    /// <summary>
    /// Reserved for domain-level services. Domain entities are currently behavior-rich POCOs,
    /// so there is no runtime service registration here.
    /// </summary>
    public static IServiceCollection RegisterDomainServices(
        this IServiceCollection services, IConfiguration config)
    {
        // Domain services (if any)
        return services;
    }

    /// <summary>
    /// Registers application services, optional AI services, and FlowEngine. AI registration
    /// comes before FlowEngine because FlowEngine agent connectors resolve the shared IChatClient.
    /// </summary>
    public static IServiceCollection RegisterApplicationServices(
        this IServiceCollection services, IConfiguration config)
    {
        AddApplicationServices(services, config);
        services.AddAiServices(config);
        // FlowEngine registered after AI so the agent client can resolve IChatClient.
        AddFlowEngineServices(services, config);
        return services;
    }

    /// <summary>
    /// Reserved extension point for host-level background services. The channel queue is
    /// registered with infrastructure support services because it is shared by audit handling.
    /// </summary>
    public static IServiceCollection RegisterBackgroundServices(
        this IServiceCollection services, IConfiguration config)
    {
        // Channel-based background queue already registered in AddSupportServices
        return services;
    }

    /// <summary>
    /// Startup tasks run after the host is built, not during DI registration, so they can
    /// resolve scoped DbContexts and tolerate local emulator startup ordering.
    /// </summary>
    private static void AddStartupTasks(IServiceCollection services)
    {
        services.AddScoped<IStartupTask, WarmupDependencies>();
        // Provision containers and tables once here instead of on every write (see the class remarks).
        services.AddScoped<IStartupTask, EnsureExternalResources>();
    }

    /// <summary>Registers support services dependencies in the service container.</summary>
    private static IServiceCollection AddSupportServices(this IServiceCollection services)
    {
        services.AddChannelBackgroundTaskQueueWithShutdownHandling();
        services.AddSingleton<IInternalMessageBus, InternalMessageBus>();
        return services;
    }
}
