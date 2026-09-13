using System.Net;
using TaskFlow.Uno.Core.Client;

namespace Test.UI;

[TestClass]
[TestCategory("Unit")]
public sealed class RuntimeGatewayConfigurationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Parse_AcceptsTheLaneNeutralGatewayContract()
    {
        Assert.AreEqual(
            "https://gateway.example.test",
            RuntimeGatewayConfiguration.Parse("""{"gatewayBaseUrl":"https://gateway.example.test/"}"""));
    }

    [TestMethod]
    public void Parse_RejectsMissingOrInvalidGatewayConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeGatewayConfiguration.Parse("{}"));
        Assert.Throws<InvalidOperationException>(() => RuntimeGatewayConfiguration.Parse("{not-json"));
        Assert.Throws<InvalidOperationException>(() => RuntimeGatewayConfiguration.Parse("""{"gatewayBaseUrl":"ftp://gateway.example.test"}"""));
    }

    [TestMethod]
    public async Task LoadAsync_RejectsMissingConfiguration()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.NotFound))
        {
            BaseAddress = new Uri("https://ui.example.test/")
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => RuntimeGatewayConfiguration.LoadAsync(client, TestContext.CancellationToken));
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
