using System.Buffers.Text;
using System.Text;
using TaskFlow.Infrastructure.Repositories;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-038 continuation-token contract for the relational TaskView read model. The token is the only thing
/// making <c>TaskViewPage.ContinuationToken</c> opaque to <c>TaskViewEndpoints</c>, and the only thing
/// stopping a caller from handing back a token minted for another tenant, so both properties are asserted
/// here rather than left to the integration lane.
/// Pure-unit tier: encode/decode only, no database.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TaskViewKeysetTokenTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "22222222-2222-2222-2222-222222222222";

    [TestMethod]
    public void Encode_ThenDecode_RoundTripsPositionExactly()
    {
        // A tick-level value: the token feeds a WHERE clause, so losing precision would skip or repeat rows.
        var lastModified = new DateTimeOffset(2026, 9, 8, 13, 45, 12, TimeSpan.Zero).AddTicks(1234567);
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var position = TaskViewKeysetToken.Decode(TaskViewKeysetToken.Encode(Tenant, lastModified, id), Tenant);

        Assert.AreEqual(lastModified, position.LastModifiedUtc);
        Assert.AreEqual(id, position.Id);
    }

    [TestMethod]
    public void Encode_NormalizesOffsetToUtc_SoTheSameInstantYieldsOneToken()
    {
        var utc = new DateTimeOffset(2026, 9, 8, 13, 45, 12, TimeSpan.Zero);
        var shifted = utc.ToOffset(TimeSpan.FromHours(5));

        Assert.AreEqual(
            TaskViewKeysetToken.Encode(Tenant, utc, "id-1"),
            TaskViewKeysetToken.Encode(Tenant, shifted, "id-1"));
    }

    [TestMethod]
    public void Decode_ForeignTenant_Throws()
    {
        var token = TaskViewKeysetToken.Encode(OtherTenant, DateTimeOffset.UnixEpoch, "id-1");

        var ex = Assert.ThrowsExactly<ArgumentException>(() => TaskViewKeysetToken.Decode(token, Tenant));
        StringAssert.Contains(ex.Message, "different tenant");
    }

    [TestMethod]
    [DataRow("not-base64-!!", DisplayName = "not Base64Url")]
    [DataRow("", DisplayName = "empty")]
    [DataRow("   ", DisplayName = "whitespace")]
    public void Decode_MalformedToken_Throws(string token) =>
        Assert.ThrowsExactly<ArgumentException>(() => TaskViewKeysetToken.Decode(token, Tenant));

    [TestMethod]
    // Every payload here is well-formed Base64Url, so only the structural checks can reject it: a truncated
    // payload, a version this build does not understand, a non-numeric or out-of-range sort key, an empty
    // tie-break key.
    [DataRow("v1|" + Tenant + "|123", DisplayName = "too few parts")]
    [DataRow("v2|" + Tenant + "|123|id-1", DisplayName = "unknown version")]
    [DataRow("v1|" + Tenant + "|not-a-number|id-1", DisplayName = "non-numeric ticks")]
    [DataRow("v1|" + Tenant + "|-1|id-1", DisplayName = "negative ticks")]
    [DataRow("v1|" + Tenant + "|9223372036854775807|id-1", DisplayName = "ticks past DateTimeOffset.MaxValue")]
    [DataRow("v1|" + Tenant + "|123|", DisplayName = "empty id")]
    public void Decode_TamperedPayload_Throws(string payload)
    {
        var token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));

        Assert.ThrowsExactly<ArgumentException>(() => TaskViewKeysetToken.Decode(token, Tenant));
    }
}
