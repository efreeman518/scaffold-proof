using System.Security.Cryptography;
using System.Text;
using TaskFlow.Infrastructure.Data.Encryption;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>D-023 acceptance from package request 5: round-trip equality, two encryptions differ, blind index stable.</summary>
[TestClass]
public sealed class ColumnEncryptionTests
{
    [TestMethod]
    public void Encrypt_RoundTrips_AndIsRandomized()
    {
        var encryptor = TestColumnEncryption.Encryptor;
        const string plaintext = "lookup-token with unicode: é中";

        var first = encryptor.Encrypt(plaintext);
        var second = encryptor.Encrypt(plaintext);

        Assert.AreEqual(plaintext, encryptor.Decrypt(first));
        Assert.AreEqual(plaintext, encryptor.Decrypt(second));
        CollectionAssert.AreNotEqual(first, second, "AES-GCM must use a fresh nonce per value.");
        Assert.AreEqual(
            AesGcmColumnEncryptor.NonceSizeBytes + Encoding.UTF8.GetByteCount(plaintext) + AesGcmColumnEncryptor.TagSizeBytes,
            first.Length);
    }

    [TestMethod]
    public void Decrypt_WithTamperedCiphertext_Throws()
    {
        var ciphertext = TestColumnEncryption.Encryptor.Encrypt("tamper-me");
        ciphertext[^1] ^= 0xFF;

        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => TestColumnEncryption.Encryptor.Decrypt(ciphertext));
    }

    [TestMethod]
    public void BlindIndex_IsStableAndKeyed()
    {
        var key = TestColumnEncryption.Keys.BlindIndexKey;
        var otherKey = RandomNumberGenerator.GetBytes(BlindIndex.SizeBytes);

        var a = BlindIndex.Compute("token", key);
        var b = BlindIndex.Compute("token", key);

        Assert.HasCount(BlindIndex.SizeBytes, a);
        CollectionAssert.AreEqual(a, b);
        CollectionAssert.AreNotEqual(a, BlindIndex.Compute("Token", key), "exact value, no normalization");
        CollectionAssert.AreNotEqual(a, BlindIndex.Compute("token", otherKey));
    }

    [TestMethod]
    public void Resolve_DerivesBlindIndexKey_WhenNotConfigured()
    {
        var options = new ColumnEncryptionOptions { LocalKeyBase64 = TestColumnEncryption.LocalKeyBase64 };

        var first = ColumnEncryptionKeys.Resolve(options);
        var second = ColumnEncryptionKeys.Resolve(options);

        Assert.IsTrue(first.IsEnabled);
        CollectionAssert.AreEqual(first.BlindIndexKey, second.BlindIndexKey, "HKDF derivation is deterministic");
        CollectionAssert.AreNotEqual(first.DataEncryptionKey, first.BlindIndexKey);
    }

    [TestMethod]
    public void Resolve_RejectsMissingOrShortKey_AndHonorsOptOut()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => ColumnEncryptionKeys.Resolve(new ColumnEncryptionOptions()));
        Assert.ThrowsExactly<InvalidOperationException>(() => ColumnEncryptionKeys.Resolve(new ColumnEncryptionOptions
        {
            LocalKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
        }));

        var disabled = ColumnEncryptionKeys.Resolve(new ColumnEncryptionOptions { Enabled = false });
        Assert.IsFalse(disabled.IsEnabled);
        Assert.AreSame(PlaintextColumnEncryptor.Instance, disabled.CreateEncryptor());
    }
}
