using EF.BackgroundServices.Attributes;
using EF.BackgroundServices.InternalMessageBus;
using System.Reflection;
using TaskFlow.Application.MessageHandlers;

namespace Test.Architecture;

/// <summary>
/// Messaging rules that a compiler cannot enforce: every in-process message handler declares its lifetime, and
/// no application type reaches past the outbox to a broker.
/// Pure-unit tier (reflection only): static assembly checks; no DI, no I/O.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class MessagingArchitectureTests : BaseTest
{
    private static readonly Assembly MessageHandlersAssembly = typeof(AuditHandler).Assembly;

    /// <summary>Verifies every IMessageHandler implementation declares its DI lifetime with the attribute.</summary>
    [TestMethod]
    public void Given_MessageHandlers_When_Scanned_Then_EveryHandlerIsAttributed()
    {
        var handlers = MessageHandlersAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>)))
            .ToList();

        Assert.IsGreaterThan(0, handlers.Count, "no IMessageHandler implementations were found to check");

        var missing = handlers
            .Where(t => t.GetCustomAttribute<ScopedMessageHandlerAttribute>() is null)
            .Select(t => t.FullName)
            .ToList();

        Assert.AreEqual(0, missing.Count,
            $"IMessageHandler implementations without [ScopedMessageHandler]: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Verifies no application type injects the broker transport. Application code raises an event on the
    /// aggregate; only the scheduler's dispatcher talks to a broker (D-026).
    /// </summary>
    [TestMethod]
    public void Given_ApplicationAssemblies_When_ConstructorsScanned_Then_NoneInjectTheTransport()
    {
        var offenders = new List<string>();

        foreach (var assembly in new[]
                 {
                     ApplicationContractsAssembly, ApplicationServicesAssembly,
                     ApplicationCqrsAssembly, MessageHandlersAssembly
                 })
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var parameter in type.GetConstructors(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                             .SelectMany(c => c.GetParameters()))
                {
                    if (parameter.ParameterType.Name == "IIntegrationEventTransport")
                        offenders.Add($"{type.FullName}.{parameter.Name}");
                }
            }
        }

        Assert.AreEqual(0, offenders.Count,
            $"application types injecting IIntegrationEventTransport: {string.Join(", ", offenders)}");
    }
}
