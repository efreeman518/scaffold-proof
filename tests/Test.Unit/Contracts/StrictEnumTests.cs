using TaskFlow.Application.Contracts;

namespace Test.Unit.Contracts;

/// <summary>
/// Contract for the configuration-switch parser every provider/mode selector uses (D4 interim). Enum.TryParse
/// accepted numeric strings and comma lists as undefined values; each selector's switch then fell through
/// silently instead of failing startup.
/// Pure-unit tier: a pure function.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class StrictEnumTests
{
    /// <summary>Verifies a declared name resolves in any case and with surrounding whitespace.</summary>
    [TestMethod]
    [DataRow("Cqrs")]
    [DataRow("cqrs")]
    [DataRow(" CQRS ")]
    public void Given_DeclaredName_When_Parsed_Then_ReturnsMember(string value) =>
        Assert.AreEqual(ApplicationStyle.Cqrs, StrictEnum.Parse<ApplicationStyle>(value, "application style"));

    /// <summary>Verifies numbers, combinations, blanks and unknown names fail with the allowed names listed.</summary>
    [TestMethod]
    [DataRow("0")]
    [DataRow("1")]
    [DataRow("5")]
    [DataRow("-1")]
    [DataRow("Service,Cqrs")]
    [DataRow(" ")]
    [DataRow(null)]
    [DataRow("Mediator")]
    public void Given_NonNameValue_When_Parsed_Then_Throws(string? value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => StrictEnum.Parse<ApplicationStyle>(value, "application style"));

        Assert.AreEqual($"Unknown application style '{value}'. Allowed values: Service, Cqrs.", ex.Message);
    }
}
