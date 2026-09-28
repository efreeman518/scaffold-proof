using System.Text.Json;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;

namespace Test.Unit;

/// <summary>
/// Verifies the shared test JSON options put the same shape on the wire as the API host (camelCase names,
/// named enums), so test request bodies look like real client requests.
/// Unit tier: pure serialization, no host.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class JsonTestOptionsTests
{
    [TestMethod]
    public void Given_ADto_When_SerializedWithTestOptions_Then_UsesCamelCaseNamesAndNamedEnums()
    {
        var json = JsonSerializer.Serialize(
            new TaskItemDto { Title = "Write tests", Priority = Priority.High },
            JsonTestOptions.Default);

        StringAssert.Contains(json, "\"title\":\"Write tests\"");
        StringAssert.Contains(json, "\"priority\":\"High\"");
    }
}
