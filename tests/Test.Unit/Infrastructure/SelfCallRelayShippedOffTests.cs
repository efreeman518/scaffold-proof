using EF.Auth.Relay;
using Microsoft.Extensions.Configuration;

namespace Test.Unit.Infrastructure;

/// <summary>
/// TaskFlow ships the workflow self-call relay (D-068) off: every host's settings leave
/// <c>FlowEngine:SelfCall:TokenScope</c> empty, the Api trusts no relaying caller and names no workflow caller, and no
/// deployment (Aspire, compose, Bicep) sets any of them, so the relay is a live-identity deployment's configuration. The
/// workflow hosts read the relay header name and allowlist from the same <c>ForwardedClaims</c> shape as the Api.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SelfCallRelayShippedOffTests
{
    private static readonly string[] RelaySettings =
        ["SelfCall__TokenScope", "SelfCall:TokenScope", "TrustedCallerIds", "Workflow__CallerIds", "Workflow:CallerIds"];

    private static readonly string[] WorkflowHosts = ["TaskFlow.Api", "TaskFlow.Scheduler", "TaskFlow.Functions"];

    [TestMethod]
    public void ShippedHostSettings_LeaveTheRelayOff()
    {
        foreach (var host in WorkflowHosts)
            Assert.AreEqual(string.Empty, Settings(host)["FlowEngine:SelfCall:TokenScope"], host);

        var api = Settings("TaskFlow.Api");
        Assert.IsEmpty(api.GetSection("ForwardedClaims:TrustedCallerIds").GetChildren());
        Assert.IsEmpty(api.GetSection("RateLimiting:Workflow:CallerIds").GetChildren());
        Assert.IsNotNull(api["RateLimiting:Tenants:Budgets:workflow:PermitLimit"], "the workflow budget a deployment turns on");
    }

    [TestMethod]
    public void WorkflowHosts_ReadTheApisRelayHeaderAndAllowlist()
    {
        var api = RelayOptions("TaskFlow.Api");
        foreach (var host in new[] { "TaskFlow.Scheduler", "TaskFlow.Functions" })
        {
            var options = RelayOptions(host);
            Assert.AreEqual(api.HeaderName, options.HeaderName, host);
            CollectionAssert.AreEquivalent(api.ClaimTypes, options.ClaimTypes, host);
        }
    }

    [TestMethod]
    public void Deployments_DoNotConfigureTheRelay()
    {
        var files = new List<string>
        {
            RepoRoot.Combine("src", "Host", "Aspire", "AppHost", "AppHost.cs"),
            RepoRoot.Combine("infra", "main.json"),
        };
        files.AddRange(Directory.GetFiles(RepoRoot.Combine("deploy", "compose"), "*.yml"));
        files.AddRange(Directory.GetFiles(RepoRoot.Combine("infra"), "*.bicep*", SearchOption.AllDirectories));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var setting in RelaySettings)
                Assert.DoesNotContain(setting, text, $"{file} configures {setting}; the relay is deployment configuration");
        }
    }

    private static IConfigurationRoot Settings(string host) =>
        new ConfigurationBuilder().AddJsonFile(RepoRoot.Combine("src", "Host", host, "appsettings.json"), optional: false).Build();

    private static ForwardedClaimsOptions RelayOptions(string host)
    {
        var options = new ForwardedClaimsOptions();
        Settings(host).GetSection(ForwardedClaimsOptions.ConfigSectionName).Bind(options);
        return options;
    }
}
