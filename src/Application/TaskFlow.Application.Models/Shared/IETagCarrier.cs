using EF.Common.Contracts;

namespace TaskFlow.Application.Models.Shared;

/// <summary>
/// Response envelope contract. <see cref="IETagVersioned.ETagVersion"/> is the aggregate version the
/// EF.AspNetCore ETag filter emits as the strong <c>ETag</c> header; <see cref="IsReplay"/> says whether the
/// response replays an existing entity for an idempotent create (D-033) so the endpoint can answer 200 instead of 201.
/// </summary>
public interface IETagCarrier : IETagVersioned
{
    /// <summary>True when this response replays an existing entity for a repeated caller-supplied id.</summary>
    bool IsReplay { get; }
}
