using System.Net.Http.Headers;

namespace TaskFlow.Application.Contracts.Storage;

/// <summary>
/// Media-type comparison for attachment content types: the type and subtype only, lower case, without parameters,
/// so "text/plain; charset=utf-8" and "TEXT/PLAIN" both match "text/plain". The attachment search filter normalizes
/// its values with <see cref="Normalize"/>, and the repository applies the same rule to the stored column.
/// </summary>
public static class AttachmentMediaType
{
    /// <summary>The lower-case "type/subtype" of <paramref name="contentType"/>, with any parameters removed.</summary>
    public static string Normalize(string contentType)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        return (separator < 0 ? contentType : contentType[..separator]).Trim().ToLowerInvariant();
    }

    /// <summary>True when <paramref name="contentType"/> parses as a concrete "type/subtype", with or without parameters.</summary>
    public static bool IsValid(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && parsed.MediaType is { } mediaType
        && !mediaType.Contains('*', StringComparison.Ordinal);
}
