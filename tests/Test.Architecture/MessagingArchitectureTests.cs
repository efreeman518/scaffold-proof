using EF.Messaging.Outbox;
using EF.Testing.Architecture;
using System.Reflection;
using TaskFlow.Application.MessageHandlers;

namespace Test.Architecture;

/// <summary>
/// Messaging rules that a compiler cannot enforce: no application type reaches past the outbox to a broker.
/// (EF.BackgroundServices 2.0 resolves every auto-registered handler from its own scope, so handler lifetime
/// needs no attribute and no rule here.)
/// Pure-unit tier (EF.Testing.Architecture reflection rules): static assembly checks; no DI, no I/O.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class MessagingArchitectureTests : BaseTest
{
    private static readonly Assembly MessageHandlersAssembly = typeof(AuditHandler).Assembly;

    /// <summary>
    /// Verifies no application type injects the broker transport. Application code raises an event on the
    /// aggregate; only the scheduler's dispatcher talks to a broker (D-026).
    /// </summary>
    [TestMethod]
    public void Given_ApplicationAssemblies_When_ConstructorsScanned_Then_NoneInjectTheTransport()
    {
        var result = ConstructorRules.MustNotInject(
            [ApplicationContractsAssembly, ApplicationServicesAssembly, ApplicationCqrsAssembly, MessageHandlersAssembly],
            type => type == typeof(IOutboxTransport));

        Assert.IsTrue(result.IsSuccessful, $"application types injecting IOutboxTransport: {result}");
    }
}
