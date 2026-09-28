using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Hosting;

namespace Test.Endpoints;

/// <summary>
/// D-060: the endpoint contract proves the default NonAzure lane, not the Azure opt-in, and boots without a container
/// runtime: the lane resolves to NonAzure with its provider defaults, and the Redis-backed Data Protection ring the
/// lane registers is replaced by the ephemeral one, so no request ever depends on the inert Redis endpoint.
/// </summary>
[TestClass]
public sealed class EndpointHostLaneTests
{
    [TestCategory("Endpoint")]
    [TestMethod]
    public void Given_CustomApiFactory_When_HostBoots_Then_DefaultNonAzureLaneRunsContainerFree()
    {
        using var factory = new CustomApiFactory();

        var lane = HostingLaneResolver.Resolve(factory.Services.GetRequiredService<IConfiguration>());

        Assert.AreEqual(HostingLane.NonAzure, lane.Lane);
        Assert.AreEqual("PostgreSql", lane.Database);
        Assert.AreEqual("RabbitMq", lane.Messaging);
        Assert.AreEqual("S3", lane.Storage);
        Assert.AreEqual("Redis", lane.DataProtection);
        Assert.IsInstanceOfType<EphemeralDataProtectionProvider>(
            factory.Services.GetRequiredService<IDataProtectionProvider>());
    }
}
