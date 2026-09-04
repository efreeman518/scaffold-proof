using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Application.Contracts.Paging;

/// <summary>
/// The decoded keyset position. <c>SortKey</c> is the serialized value of the mode's leading sort
/// column; <c>LastId</c> is the tiebreaker that makes the order total.
/// </summary>
public sealed record CursorToken(TaskItemSortMode SortMode, Guid TenantId, string SortKey, Guid LastId);

/// <summary>
/// Turns a keyset position into an opaque, tamper-evident cursor and back. Callers must not be able
/// to hand-craft a cursor for another tenant or another sort mode, so the payload is authenticated
/// and both are re-checked on unprotect.
/// </summary>
public interface ICursorProtector
{
    /// <summary>Encodes a keyset position as an opaque cursor string.</summary>
    string Protect(CursorToken token);

    /// <summary>
    /// Decodes a cursor. Returns false for tampering, expiry, a foreign tenant, or a sort mode that
    /// does not match the current request - all of which are answered with 400, never a silent reset
    /// to page one (which would duplicate rows the caller already read).
    /// </summary>
    bool TryUnprotect(string cursor, TaskItemSortMode expectedSortMode, Guid tenantId, out CursorToken token);
}
