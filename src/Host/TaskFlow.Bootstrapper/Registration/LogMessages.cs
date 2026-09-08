using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Source-generated logging methods for the Bootstrapper. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that a Foundry Local model is being downloaded if needed.</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 1, Level = LogLevel.Information, Message = "Downloading Foundry Local model {ModelAlias} if needed.")]
    public static partial void DownloadingFoundryModel(this ILogger logger, string modelAlias);

    /// <summary>Logs that a Foundry Local model is being loaded.</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 2, Level = LogLevel.Information, Message = "Loading Foundry Local model {ModelId}.")]
    public static partial void LoadingFoundryModel(this ILogger logger, string modelId);

    /// <summary>Logs that the Azure AI Foundry chat client is being configured.</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 3, Level = LogLevel.Information, Message = "{AppName} {Environment} - Configure Azure AI Foundry chat client.")]
    public static partial void ConfigureAzureChatClient(this ILogger logger, string appName, string environment);

    /// <summary>Logs that the Foundry Local chat client is being configured.</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 4, Level = LogLevel.Information, Message = "{AppName} {Environment} - Configure Foundry Local chat client.")]
    public static partial void ConfigureFoundryLocalChatClient(this ILogger logger, string appName, string environment);

    /// <summary>Logs that a provisioned external resource exists and is usable.</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 5, Level = LogLevel.Information, Message = "External resource ready: {ResourceKind} {ResourceName}")]
    public static partial void ExternalResourceReady(this ILogger logger, string resourceKind, string resourceName);

    /// <summary>Logs that FlowEngineIfMatchOverrideHandler applied the D-032 trusted-automation If-Match override.</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 6, Level = LogLevel.Information, Message = "FlowEngine If-Match override applied: {Method} {Route}")]
    public static partial void FlowEngineIfMatchOverrideApplied(this ILogger logger, string method, string route);

    /// <summary>Logs which Data Protection key-ring persistence backend was configured.</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 7, Level = LogLevel.Information, Message = "{AppName} {Environment} - Configure Data Protection key-ring persistence: {Persistence}.")]
    public static partial void ConfigureDataProtectionPersistence(this ILogger logger, string appName, string environment, string persistence);

    /// <summary>Logs that no Data Protection key-ring persistence is configured (D-043).</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 8, Level = LogLevel.Warning, Message = "{AppName} {Environment} - No Data Protection key-ring persistence configured; DataProtectionCursorProtector-issued cursors will not survive a restart or reach other replicas.")]
    public static partial void DataProtectionPersistenceNone(this ILogger logger, string appName, string environment);

    /// <summary>Logs that the OpenAI-compatible chat client is being configured (D-041).</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 9, Level = LogLevel.Information, Message = "{AppName} {Environment} - Configure OpenAI-compatible chat client at {Endpoint}.")]
    public static partial void ConfigureOpenAICompatibleChatClient(this ILogger logger, string appName, string environment, string endpoint);

    /// <summary>Logs that a legacy AiServices:Use* kill switch is superseded by dynamic feature flags (D-042).</summary>
    [LoggerMessage(EventId = LogEventIds.BootstrapperBase + 10, Level = LogLevel.Warning, Message = "{AppName} {Environment} - {ConfigKey}=false is a legacy AI kill switch superseded by dynamic feature flags (D-042); honored one more release, then removed. Configure App Configuration/appsettings feature flags instead.")]
    public static partial void LegacyAiSwitchSuperseded(this ILogger logger, string appName, string environment, string configKey);
}
