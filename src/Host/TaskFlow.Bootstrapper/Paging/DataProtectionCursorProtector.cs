using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using System.Globalization;
using System.Text;
using TaskFlow.Application.Contracts.Paging;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Bootstrapper.Paging;

/// <summary>
/// Cursor protection over ASP.NET Data Protection. The payload is authenticated, so a client cannot
/// forge a position, and the tenant and sort mode are re-checked after decryption, so a cursor minted
/// for one tenant or one ordering is rejected instead of quietly returning another tenant's rows.
///
/// Key-ring caveat: cursors survive process restarts and reach sibling replicas only when the key ring
/// is persisted and shared - which production does through Blob + Key Vault (Program.cs
/// ConfigureDataProtection). With the default in-memory ring (local dev, tests, an unconfigured
/// deployment) an old cursor decrypts to garbage after a restart and is answered with 400, which is
/// correct but surprising. The alternative is an HMAC over a configured "Paging:CursorKey"; it was not
/// taken because production already has the shared ring and a second key to rotate is a second thing
/// to get wrong.
/// </summary>
internal sealed class DataProtectionCursorProtector : ICursorProtector
{
    // Versioned purpose string: changing the cursor payout format means changing this, which
    // invalidates every outstanding cursor rather than mis-parsing one.
    private const string Purpose = "TaskFlow.Cursor.v1";
    private const char Separator = '|';

    private readonly IDataProtector _protector;

    /// <summary>Initializes the protector from the application's data protection provider.</summary>
    public DataProtectionCursorProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose);

    /// <inheritdoc />
    public string Protect(CursorToken token)
    {
        // SortKey is the only free-form field; it never contains the separator (round-trip timestamp,
        // integer, or "~"), so a plain join is unambiguous.
        var payload = string.Join(Separator,
            ((int)token.SortMode).ToString(CultureInfo.InvariantCulture),
            token.TenantId.ToString("N"),
            token.SortKey,
            token.LastId.ToString("N"));

        return WebEncoders.Base64UrlEncode(_protector.Protect(Encoding.UTF8.GetBytes(payload)));
    }

    /// <inheritdoc />
    public bool TryUnprotect(string cursor, TaskItemSortMode expectedSortMode, Guid tenantId, out CursorToken token)
    {
        token = null!;
        if (string.IsNullOrWhiteSpace(cursor)) return false;

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(_protector.Unprotect(WebEncoders.Base64UrlDecode(cursor)));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            // Tampered, truncated, or minted under a key ring this process cannot read. Both are a
            // client-visible 400; nothing here is recoverable by retrying.
            return false;
        }

        var parts = payload.Split(Separator);
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sortMode)) return false;
        if (!Guid.TryParseExact(parts[1], "N", out var cursorTenantId)) return false;
        if (!Guid.TryParseExact(parts[3], "N", out var lastId)) return false;

        if (sortMode != (int)expectedSortMode) return false;
        if (cursorTenantId != tenantId) return false;

        token = new CursorToken((TaskItemSortMode)sortMode, cursorTenantId, parts[2], lastId);
        return true;
    }
}
