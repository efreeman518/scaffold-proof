using Aspire.Hosting.Publishing;
using System.Security.Cryptography;

namespace AppHost;

/// <summary>
/// Generates a random 32-byte key as base64 for the column-encryption parameters (D-023). With <c>persist: true</c>
/// Aspire stores the generated value in the AppHost user secrets, so the same key decrypts rows across restarts of the
/// persistent local database volume. Production supplies <c>Database__Encryption__*</c> from Key Vault instead; the
/// manifest emits an equivalent random-string generator for publish parity only.
/// </summary>
internal sealed class Base64KeyParameterDefault : ParameterDefault
{
    private static readonly GenerateParameterDefault ManifestShape = new() { MinLength = 44, Special = false };

    public override string GetDefaultValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public override void WriteToManifest(ManifestPublishingContext context) => ManifestShape.WriteToManifest(context);
}
