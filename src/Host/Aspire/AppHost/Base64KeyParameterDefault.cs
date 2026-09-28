using Aspire.Hosting.Publishing;
using System.Security.Cryptography;

namespace AppHost;

/// <summary>
/// Generates a random 32-byte key as base64 for the column-encryption parameters (D-023). With <c>persist: true</c>
/// Aspire stores the generated value in the AppHost user secrets, so the same key decrypts rows across restarts of the
/// persistent local database volume. Production supplies <c>Database__Encryption__*</c> from Key Vault instead.
/// <para>
/// There is no manifest equivalent: a manifest <c>generate</c> default yields random characters, and 44 of them decode
/// to 33 bytes, which the column encryptor rejects (it requires exactly 32), so a manifest-based deployment would
/// start every host with an invalid key. Manifest publishing therefore fails loudly instead.
/// </para>
/// </summary>
internal sealed class Base64KeyParameterDefault : ParameterDefault
{
    public override string GetDefaultValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public override void WriteToManifest(ManifestPublishingContext context) =>
        throw new NotSupportedException(
            "Column key parameters have no manifest generator; supply the value from a secret store.");
}
