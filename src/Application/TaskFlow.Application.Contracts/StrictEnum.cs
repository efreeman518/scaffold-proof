namespace TaskFlow.Application.Contracts;

/// <summary>
/// Parses a configuration switch into an enum by declared name only. <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
/// also accepts numeric strings ("2", "-1") and comma lists, which yield undefined values that every
/// <c>switch</c>/<c>==</c> on the result silently treats as "none of the above".
/// </summary>
// shortcut: app-local until EF.Common ships StrictEnum (EF.Packages 2.0 D4); no [Flags] support because no
// TaskFlow switch is a flags enum.
public static class StrictEnum
{
    /// <summary>
    /// Returns the member whose name matches <paramref name="value"/> case-insensitively after trimming;
    /// anything else, including numbers and combinations, throws with the allowed names.
    /// </summary>
    public static TEnum Parse<TEnum>(string? value, string settingName) where TEnum : struct, Enum
    {
        var candidate = value?.Trim();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<TEnum>(name);
        }

        throw new ArgumentException(
            $"Unknown {settingName} '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}
