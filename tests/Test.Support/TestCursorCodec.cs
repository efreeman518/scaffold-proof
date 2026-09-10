using EF.Data.Contracts;
using System.Security.Cryptography;

namespace Test.Support;

/// <summary>
/// The keyset cursor codec every test shares, keyed exactly as a host keys it: HKDF over the fixed test
/// column-encryption DEK under the production info label (see <c>RegisterServices.AddSharedApplicationServices</c>).
/// A cursor minted here is therefore accepted by a test host's own codec and vice versa.
/// </summary>
public static class TestCursorCodec
{
    public static readonly CursorCodec Instance = new(HKDF.DeriveKey(
        HashAlgorithmName.SHA256,
        TestColumnEncryption.Keys.DataEncryptionKey!,
        CursorCodec.SignatureSizeBytes,
        info: "TaskFlow.Cursor.v1"u8.ToArray()));
}
