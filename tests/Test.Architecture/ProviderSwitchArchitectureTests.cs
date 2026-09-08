using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetArchTest.Rules;
using System.Reflection;
using TaskFlow.Bootstrapper;

namespace Test.Architecture;

/// <summary>
/// Architecture rules for the D-035..D-045 provider switches: cloud-SDK namespaces stay confined to the
/// layers allowed to use them, and every switch discovered via <see cref="ProviderSwitchAttribute"/>
/// leaves its contract resolvable in the default (unconfigured) arm - the same property the "S3 is not
/// implemented yet" style throws would otherwise silently defeat if a switch shipped with no fallback at
/// all. Search (Infrastructure.AI) is not attribute-tagged - tagging it would require Infrastructure.AI to
/// reference TaskFlow.Bootstrapper, an inversion of the existing dependency direction - so its default-arm
/// coverage lives in Test.Unit's SearchProviderSelectorTests instead; this file only asserts its namespace
/// boundary here.
/// Pure-unit tier (NetArchTest + reflection): no I/O beyond building an in-memory ServiceCollection.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class ProviderSwitchArchitectureTests : BaseTest
{
    private static readonly Assembly InfrastructureAiAssembly = typeof(TaskFlow.Infrastructure.AI.TaskFlowAiSettings).Assembly;
    private static readonly Assembly InfrastructureStorageAssembly = typeof(TaskFlow.Infrastructure.Storage.NoOpAuditLogRepository).Assembly;
    private static readonly Assembly InfrastructureCachingAssembly = typeof(TaskFlow.Infrastructure.Caching.RegisterCachingServices).Assembly;
    private static readonly Assembly BootstrapperAssembly = typeof(RegisterServices).Assembly;

    /// <summary>Application and Domain assemblies must never reference Azure or Amazon SDK namespaces directly.</summary>
    [TestMethod]
    public void Given_ApplicationAndDomainAssemblies_When_DependenciesChecked_Then_NoCloudSdkNamespaceUsage()
    {
        foreach (var assembly in new[]
                 {
                     DomainModelAssembly, ApplicationContractsAssembly, ApplicationServicesAssembly, ApplicationCqrsAssembly
                 })
        {
            AssertNoDependencyOnAny(assembly, "Azure", "Microsoft.Azure", "Amazon");
        }
    }

    /// <summary>Only Infrastructure.Storage may reference the AWS SDK; sibling Infrastructure projects must not.</summary>
    [TestMethod]
    public void Given_SiblingInfrastructureAssemblies_When_DependenciesChecked_Then_NoAmazonNamespaceUsage()
    {
        foreach (var assembly in new[]
                 {
                     InfrastructureDataAssembly, InfrastructureRepositoriesAssembly, InfrastructureAiAssembly,
                     InfrastructureCachingAssembly, BootstrapperAssembly
                 })
        {
            AssertNoDependencyOnAny(assembly, "Amazon");
        }
    }

    /// <summary>
    /// Every dispatcher tagged <see cref="ProviderSwitchAttribute"/> is discovered, and its default
    /// (unconfigured) arm registers <see cref="ProviderSwitchAttribute.ContractType"/> without throwing.
    /// </summary>
    [TestMethod]
    public void Given_ProviderSwitchDispatchers_When_InvokedUnconfigured_Then_ContractTypeResolves()
    {
        var config = new ConfigurationBuilder().Build();
        var tagged = GetLoadableTypes(BootstrapperAssembly)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Switch: m.GetCustomAttribute<ProviderSwitchAttribute>()))
            .Where(x => x.Switch is not null)
            .ToList();

        Assert.IsGreaterThan(0, tagged.Count, "no [ProviderSwitch]-tagged dispatchers were found to check");

        var failures = new List<string>();
        foreach (var (method, providerSwitch) in tagged)
        {
            try
            {
                var contractType = providerSwitch!.ContractType;
                var parameters = method.GetParameters();

                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(IServiceCollection))
                {
                    var services = new ServiceCollection();
                    services.AddLogging();
                    method.Invoke(null, [services, config]);
                    if (!services.Any(d => d.ServiceType == contractType))
                        failures.Add($"{method.Name}: {contractType.Name} not registered by the default arm");
                }
                else if (parameters.Length >= 1 && typeof(IHostApplicationBuilder).IsAssignableFrom(parameters[0].ParameterType))
                {
                    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                    {
                        ApplicationName = "Test.Architecture",
                        EnvironmentName = "Testing",
                        DisableDefaults = true
                    });
                    method.Invoke(null, [builder, NullLogger.Instance]);
                    var provider = builder.Services.BuildServiceProvider();
                    if (provider.GetService(contractType) is null)
                        failures.Add($"{method.Name}: {contractType.Name} not registered by the default arm");
                }
                else
                {
                    failures.Add($"{method.Name}: unrecognized dispatcher signature for reflection invocation");
                }
            }
            catch (TargetInvocationException ex)
            {
                failures.Add($"{method.Name}: threw {ex.InnerException?.GetType().Name} on the default arm: {ex.InnerException?.Message}");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join("; ", failures));
    }

    /// <summary>
    /// <see cref="Assembly.GetTypes"/> throws if any type in the assembly cannot load (here: FoundryLocalChatClient,
    /// whose Microsoft.AI.Foundry.Local dependency is PrivateAssets="all" and so absent from this test
    /// project's output). The types this test actually needs (RegisterServices) load fine regardless.
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static void AssertNoDependencyOnAny(Assembly assembly, params string[] namespaces)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(namespaces).GetResult();
        Assert.IsTrue(result.IsSuccessful,
            $"{assembly.GetName().Name} has a forbidden dependency on {string.Join("/", namespaces)}: {FormatFailingTypes(result)}");
    }

    private static string FormatFailingTypes(NetArchTest.Rules.TestResult result) =>
        result.FailingTypes != null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";
}
