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

    /// <summary>ETag currency for this response - the aggregate version carried by the item.</summary>
    [JsonIgnore]
    public long? ETagVersion => Item is EntityBaseDto dto ? dto.Version : null;
}
