using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Infrastructure.Data.Encryption;

/// <summary>
/// Source-generated logging methods for the Infrastructure.Data.Encryption layer. Using
/// <see cref="LoggerMessageAttribute"/> defers argument evaluation until the log level is enabled,
/// satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that column encryption is disabled and secure columns are stored as plaintext.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureDataBase + 1, Level = LogLevel.Warning, Message = "Column encryption is disabled ({Section}:Enabled=false); secure columns are stored as plaintext.")]
    public static partial void ColumnEncryptionDisabled(this ILogger logger, string section);
}
