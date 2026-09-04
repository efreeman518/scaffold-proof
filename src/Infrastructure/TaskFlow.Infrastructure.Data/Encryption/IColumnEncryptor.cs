using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Security.Cryptography;
using System.Text;

namespace TaskFlow.Infrastructure.Data.Encryption;

// fallback: replace with EF.Data.Encryption.IColumnEncryptor when published (package request 5).
/// <summary>
/// D-023: provider-neutral application-layer column encryption. One instance per process; the model's value
/// converters capture it, so it is resolved through <see cref="ColumnEncryptionOptionsExtension"/> in OnModelCreating.
/// </summary>
public interface IColumnEncryptor
{
    byte[] Encrypt(string plaintext);
    string Decrypt(byte[] ciphertext);
    ValueConverter<string?, byte[]?> StringConverter { get; }
}

// fallback: replace with EF.Data.Encryption.AesGcmColumnEncryptor when published (package request 5).
/// <summary>AES-256-GCM, randomized: each value is stored as nonce (12) || ciphertext || tag (16).</summary>
public sealed class AesGcmColumnEncryptor : IColumnEncryptor
{
    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public AesGcmColumnEncryptor(byte[] key)
    {
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"AES-256-GCM requires a {KeySizeBytes}-byte key; got {key.Length} bytes.", nameof(key));
        _key = key;
        StringConverter = new ValueConverter<string?, byte[]?>(v => Encrypt(v!), v => Decrypt(v!));
    }

    public ValueConverter<string?, byte[]?> StringConverter { get; }

    public byte[] Encrypt(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSizeBytes + plaintextBytes.Length + TagSizeBytes];
        var nonce = output.AsSpan(0, NonceSizeBytes);
        var ciphertext = output.AsSpan(NonceSizeBytes, plaintextBytes.Length);
        var tag = output.AsSpan(NonceSizeBytes + plaintextBytes.Length, TagSizeBytes);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSizeBytes);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        return output;
    }

    public string Decrypt(byte[] ciphertext)
    {
        if (ciphertext.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Ciphertext is shorter than nonce + tag.");

        var nonce = ciphertext.AsSpan(0, NonceSizeBytes);
        var payload = ciphertext.AsSpan(NonceSizeBytes, ciphertext.Length - NonceSizeBytes - TagSizeBytes);
        var tag = ciphertext.AsSpan(ciphertext.Length - TagSizeBytes, TagSizeBytes);
        var plaintext = new byte[payload.Length];

        using var aes = new AesGcm(_key, TagSizeBytes);
        aes.Decrypt(nonce, payload, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}

/// <summary>
/// UTF-8 passthrough used only where no encryptor is configured: design-time model building, InMemory test
/// contexts, and the explicit <c>Database:Encryption:Enabled=false</c> opt-out (logged at startup). The
/// Bootstrapper always registers the real encryptor for runtime hosts.
/// </summary>
public sealed class PlaintextColumnEncryptor : IColumnEncryptor
{
    public static readonly PlaintextColumnEncryptor Instance = new();

    private PlaintextColumnEncryptor()
    {
        StringConverter = new ValueConverter<string?, byte[]?>(v => Encrypt(v!), v => Decrypt(v!));
    }

    public ValueConverter<string?, byte[]?> StringConverter { get; }

    public byte[] Encrypt(string plaintext) => Encoding.UTF8.GetBytes(plaintext);

    public string Decrypt(byte[] ciphertext) => Encoding.UTF8.GetString(ciphertext);
}
