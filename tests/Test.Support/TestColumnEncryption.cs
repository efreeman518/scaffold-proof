using TaskFlow.Infrastructure.Data.Encryption;

namespace Test.Support;

/// <summary>
/// Fixed, obviously-not-secret column encryption keys shared by every test host and fixture so encrypted rows
/// written by one context decrypt in another. Never used outside tests.
/// </summary>
public static class TestColumnEncryption
{
    // 32 bytes each: "TaskFlowTestColumnEncryptionKey!" and "TaskFlowTestBlindIndexHmacKey!!!" as base64.
    public const string LocalKeyBase64 = "VGFza0Zsb3dUZXN0Q29sdW1uRW5jcnlwdGlvbktleSE=";
    public const string BlindIndexKeyBase64 = "VGFza0Zsb3dUZXN0QmxpbmRJbmRleEhtYWNLZXkhISE=";

    public static readonly ColumnEncryptionKeys Keys = ColumnEncryptionKeys.Resolve(new ColumnEncryptionOptions
    {
        LocalKeyBase64 = LocalKeyBase64,
        BlindIndexKeyBase64 = BlindIndexKeyBase64
    });

    public static readonly IColumnEncryptor Encryptor = Keys.CreateEncryptor();

    /// <summary>Configuration entries for hosts booted through the Bootstrapper (<c>Database:Encryption</c>).</summary>
    public static IReadOnlyDictionary<string, string?> Configuration { get; } = new Dictionary<string, string?>
    {
        [$"{ColumnEncryptionOptions.SectionName}:LocalKeyBase64"] = LocalKeyBase64,
        [$"{ColumnEncryptionOptions.SectionName}:BlindIndexKeyBase64"] = BlindIndexKeyBase64
    };

    /// <summary>Environment-variable form for hosts that only accept env overrides.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>
    {
        ["Database__Encryption__LocalKeyBase64"] = LocalKeyBase64,
        ["Database__Encryption__BlindIndexKeyBase64"] = BlindIndexKeyBase64
    };
}
