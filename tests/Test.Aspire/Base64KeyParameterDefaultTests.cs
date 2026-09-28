using AppHost;

namespace Test.Aspire;

/// <summary>
/// D-023 column keys are exactly 32 bytes. The local generator produces that; a manifest generator cannot (44
/// random characters decode to 33 bytes), so manifest publishing must refuse instead of emitting a key every host
/// would reject at startup. Pure: no AppHost is built and no container is started.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class Base64KeyParameterDefaultTests
{
    [TestMethod]
    public void GetDefaultValue_IsExactlyThirtyTwoBytesOfBase64()
    {
        var key = new Base64KeyParameterDefault().GetDefaultValue();

        Assert.HasCount(32, Convert.FromBase64String(key));
    }

    [TestMethod]
    public void WriteToManifest_RefusesBecauseNoGeneratorYieldsAThirtyTwoByteKey() =>
        Assert.ThrowsExactly<NotSupportedException>(() => new Base64KeyParameterDefault().WriteToManifest(null!));
}
