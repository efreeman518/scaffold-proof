namespace TaskFlow.Application.Models.Shared;

/// <summary>
/// Response envelope contract the host ETag filter reads. Implementations expose the aggregate
/// version that becomes the strong <c>ETag</c> header, plus whether the response is an idempotent
/// create replay (D-033) so the endpoint can answer 200 instead of 201.
/// </summary>
public interface IETagCarrier
{
    /// <summary>Aggregate version to emit as the response ETag, or null when the payload carries none.</summary>
    long? ETagVersion { get; }

    /// <summary>True when this response replays an existing entity for a repeated caller-supplied id.</summary>
    bool IsReplay { get; }
}
