using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using System.Security.Cryptography;

namespace TaskFlow.Infrastructure.Data.Encryption;

// fallback: replace with EF.Data.Encryption.ColumnEncryptionOptions when published (package request 5).
/// <summary>Bound from <c>Database:Encryption</c>.</summary>
public sealed class ColumnEncryptionOptions
{
    public const string SectionName = "Database:Encryption";

    /// <summary>Default on. Explicit false stores the secure columns as plaintext UTF-8 (logged at startup).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local/dev data-encryption key: base64 of exactly 32 bytes. Ignored when <see cref="KeyVaultKeyUrl"/> is set.</summary>
    public string? LocalKeyBase64 { get; set; }

    /// <summary>Optional blind-index key (base64, 32 bytes). Derived from the DEK with HKDF when omitted.</summary>
    public string? BlindIndexKeyBase64 { get; set; }

    /// <summary>Azure Key Vault RSA key URL that wraps <see cref="WrappedDekBase64"/> (RSA-OAEP).</summary>
    public string? KeyVaultKeyUrl { get; set; }

    /// <summary>The RSA-OAEP-wrapped 32-byte DEK, base64.</summary>
    public string? WrappedDekBase64 { get; set; }
}

/// <summary>Resolved key material, built once per process. <see cref="Disabled"/> is the explicit opt-out.</summary>
public sealed record ColumnEncryptionKeys(byte[]? DataEncryptionKey, byte[] BlindIndexKey)
{
    /// <summary>Plaintext storage; blind indexes are still computed (unkeyed HMAC) so equality lookups keep working.</summary>
    public static readonly ColumnEncryptionKeys Disabled = new(null, []);

    public bool IsEnabled => DataEncryptionKey is not null;

    public IColumnEncryptor CreateEncryptor() =>
        DataEncryptionKey is null ? PlaintextColumnEncryptor.Instance : new AesGcmColumnEncryptor(DataEncryptionKey);

    public static ColumnEncryptionKeys Resolve(ColumnEncryptionOptions options)
    {
        if (!options.Enabled) return Disabled;

        var dek = !string.IsNullOrWhiteSpace(options.KeyVaultKeyUrl)
            ? KeyVaultDekProvider.Unwrap(options.KeyVaultKeyUrl, RequireBase64(options.WrappedDekBase64, nameof(options.WrappedDekBase64), null))
            : RequireBase64(options.LocalKeyBase64, nameof(options.LocalKeyBase64), AesGcmColumnEncryptor.KeySizeBytes);

        var blindIndexKey = options.BlindIndexKeyBase64 is null
            ? HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, BlindIndex.SizeBytes, info: "TaskFlow.BlindIndex"u8.ToArray())
            : RequireBase64(options.BlindIndexKeyBase64, nameof(options.BlindIndexKeyBase64), BlindIndex.SizeBytes);

        return new ColumnEncryptionKeys(dek, blindIndexKey);
    }

    private static byte[] RequireBase64(string? value, string name, int? expectedLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"{ColumnEncryptionOptions.SectionName}:{name} is required while column encryption is enabled.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{ColumnEncryptionOptions.SectionName}:{name} is not valid base64.", ex);
        }

        if (expectedLength is int length && bytes.Length != length)
            throw new InvalidOperationException(
                $"{ColumnEncryptionOptions.SectionName}:{name} must decode to {length} bytes; got {bytes.Length}.");

        return bytes;
    }
}

// fallback: replace with EF.Data.Encryption.KeyVaultDekProvider when published (package request 5).
/// <summary>
/// Unwraps the data-encryption key with an Azure Key Vault RSA key (RSA-OAEP). Called once at startup; the
/// plaintext DEK then lives in process memory only. Requires the host identity to hold the Key Vault Crypto User role.
/// </summary>
public static class KeyVaultDekProvider
{
    public static byte[] Unwrap(string keyVaultKeyUrl, byte[] wrappedDek)
    {
        var client = new CryptographyClient(new Uri(keyVaultKeyUrl), new DefaultAzureCredential());
        var dek = client.UnwrapKey(KeyWrapAlgorithm.RsaOaep, wrappedDek).Key;
        if (dek.Length != AesGcmColumnEncryptor.KeySizeBytes)
            throw new InvalidOperationException(
                $"Key Vault unwrapped a {dek.Length}-byte DEK; AES-256-GCM needs {AesGcmColumnEncryptor.KeySizeBytes} bytes.");
        return dek;
    }
}
