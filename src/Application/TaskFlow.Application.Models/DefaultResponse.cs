using System.Text.Json.Serialization;
using TaskFlow.Application.Models.Shared;

namespace TaskFlow.Application.Models;

/// <summary>Provides default response behavior for the Application layer.</summary>
public record DefaultResponse<T> : IETagCarrier
{
    public DefaultResponse() { }
    public DefaultResponse(T? item) { Item = item; }

    public T? Item { get; init; }
    public TenantInfoDto? TenantInfo { get; init; }

    /// <summary>Set by create handlers when a caller-supplied id replayed an existing entity (D-033).</summary>
    [JsonIgnore]
    public bool IsReplay { get; init; }

    /// <summary>
    /// Root aggregate version, set by child responses (D-031). A comment or checklist item carries its
    /// own row Version for display, but the ETag currency of the whole aggregate is the root's, so
    /// this wins when present.
    /// </summary>
    [JsonIgnore]
    public long? AggregateVersion { get; init; }

    /// <summary>ETag currency for this response.</summary>
    [JsonIgnore]
    public long? ETagVersion => AggregateVersion ?? (Item is EntityBaseDto dto ? dto.Version : null);
}
