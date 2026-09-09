namespace TaskFlow.Bootstrapper;

/// <summary>
/// Marks a provider-switch dispatcher method (<c>Add&lt;X&gt;Services(IServiceCollection, IConfiguration)</c>)
/// so <c>Test.Architecture</c> can discover every switch by reflection and confirm its default
/// (unconfigured) arm always registers <see cref="ContractType"/> without throwing (D-035..D-045).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ProviderSwitchAttribute(Type contractType) : Attribute
{
    /// <summary>The service contract this switch must leave resolvable in every implemented arm.</summary>
    public Type ContractType { get; } = contractType;
}
