using Microsoft.Extensions.Configuration;
using TaskFlow.Bootstrapper;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-051: Cosmos cross-region hedging is a deployment decision, so the default must stay off - it multiplies
/// request units against a slow region and does nothing at all on a single-region account.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CosmosHedgingOptionsTests
{
    /// <summary>No configuration means no client options at all, i.e. the SDK defaults.</summary>
    [TestMethod]
    public void BuildCosmosClientOptions_WithoutConfiguration_IsNull()
    {
        Assert.IsNull(RegisterServices.BuildCosmosClientOptions(Config(null)));
    }

    /// <summary>Enabled attaches the cross-region hedging availability strategy.</summary>
    [TestMethod]
    public void BuildCosmosClientOptions_WhenEnabled_SetsCrossRegionHedging()
    {
        var options = RegisterServices.BuildCosmosClientOptions(Config(new Dictionary<string, string?>
        {
            ["Cosmos:Hedging:Enabled"] = "true",
            ["Cosmos:Hedging:ThresholdMs"] = "400",
            ["Cosmos:Hedging:ThresholdStepMs"] = "80"
        }));

        Assert.IsNotNull(options);
        Assert.IsNotNull(options.AvailabilityStrategy);
    }

    private static IConfiguration Config(Dictionary<string, string?>? values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();
}
