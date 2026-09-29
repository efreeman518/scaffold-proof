using EF.Common;

namespace TaskFlow.Application.Contracts;

/// <summary>
/// Selects the application path used behind the same HTTP and DTO contract.
/// Service uses the application-service classes; Cqrs routes requests through request handlers.
/// </summary>
public enum ApplicationStyle
{
    Service,
    Cqrs
}

/// <summary>
/// Resolves application style from TASKFLOW_APPLICATION_STYLE first, then configuration.
/// This lets tests and Aspire runs switch the whole app without changing appsettings files.
/// </summary>
public static class ApplicationStyleResolver
{
    public const string ConfigKey = "Application:Style";
    public const string EnvironmentVariable = "TASKFLOW_APPLICATION_STYLE";
    public const ApplicationStyle DefaultStyle = ApplicationStyle.Service;

    /// <summary>
    /// Returns the configured style or the default Service path. Invalid values fail fast so
    /// startup does not silently run the wrong endpoint mapping.
    /// </summary>
    public static ApplicationStyle Resolve(string? configuredStyle)
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = configuredStyle;
        }

        return StrictEnum.ParseOrDefault(raw, DefaultStyle, "application style");
    }
}
