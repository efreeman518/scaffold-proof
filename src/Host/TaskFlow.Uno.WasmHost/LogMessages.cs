using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Uno.WasmHost;

/// <summary>
/// Source-generated logging methods for the Uno WASM static-asset host. Using
/// <see cref="LoggerMessageAttribute"/> defers argument evaluation until the log level is enabled,
/// satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that the built Uno WASM assets were not found at the resolved dist path.</summary>
    [LoggerMessage(EventId = LogEventIds.UnoWasmHostBase + 1, Level = LogLevel.Warning, Message = "Uno WASM assets were not found at {DistPath}. Build TaskFlow.Uno for net10.0-browserwasm first.")]
    public static partial void UnoWasmAssetsNotFound(this ILogger logger, string distPath);
}
