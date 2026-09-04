using EF.Common.Contracts;

namespace TaskFlow.Application.Models.Shared;

/// <summary>Carries entity base data across API, application, and UI boundaries.</summary>
public abstract record EntityBaseDto : IEntityBaseDto
{
    public Guid? Id { get; set; }

    /// <summary>
    /// App-managed optimistic concurrency token (D-021). Surfaced as the strong HTTP ETag of the
    /// owning aggregate and echoed back as If-Match on writes. Null on inbound create payloads.
    /// </summary>
    public long? Version { get; set; }
}
