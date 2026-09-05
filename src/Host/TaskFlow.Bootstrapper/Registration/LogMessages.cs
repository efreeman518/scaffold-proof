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
}
