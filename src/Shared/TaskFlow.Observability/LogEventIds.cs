namespace TaskFlow.Observability;

/// <summary>
/// Base offsets for <c>ILogger</c> EventIds, one per subsystem, so that source-generated
/// <c>[LoggerMessage]</c> events stay unique and stable when logs from every project are
/// aggregated into a single sink (Application Insights, the Aspire dashboard, Seq, etc.).
/// <para>
/// Each area owns a 1000-wide range starting at 10000. The non-zero base avoids colliding
/// with low-numbered EventIds emitted by framework/third-party libraries, and the per-area
/// buckets leave room to grow. Within a project, declare events as <c>Base + n</c> and never
/// renumber a shipped EventId (treat it like a public API: deprecate rather than reuse).
/// </para>
/// <para>
/// This type lives in a dependency-free shared library so every layer (domain, application,
/// infrastructure, and hosts) can reference it without introducing improper coupling.
/// </para>
/// </summary>
public static class LogEventIds
{
    /// <summary>API host and middleware. Range 10000-10999.</summary>
    public const int ApiBase = 10000;

    /// <summary>Bootstrapper service registration. Range 11000-11999.</summary>
    public const int BootstrapperBase = 11000;

    /// <summary>Gateway (reverse proxy / token service). Range 12000-12999.</summary>
    public const int GatewayBase = 12000;

    /// <summary>Azure Functions host triggers. Range 13000-13999.</summary>
    public const int FunctionsBase = 13000;

    /// <summary>Scheduler background jobs. Range 14000-14999.</summary>
    public const int SchedulerBase = 14000;

    /// <summary>Infrastructure.AI (agents, search, reviewers). Range 15000-15999.</summary>
    public const int InfrastructureAiBase = 15000;

    /// <summary>Infrastructure.Storage (Cosmos, audit, Service Bus). Range 16000-16999.</summary>
    public const int InfrastructureStorageBase = 16000;

    /// <summary>Application.Cqrs feature handlers. Range 17000-17999.</summary>
    public const int ApplicationCqrsBase = 17000;

    /// <summary>Application.Services (projections and services). Range 18000-18999.</summary>
    public const int ApplicationServicesBase = 18000;

    /// <summary>Application.MessageHandlers (integration event handlers). Range 19000-19999.</summary>
    public const int ApplicationMessageHandlersBase = 19000;

    /// <summary>Infrastructure.Messaging.RabbitMq (topology, transport adapter). Range 20000-20999.</summary>
    public const int InfrastructureMessagingRabbitMqBase = 20000;
}
