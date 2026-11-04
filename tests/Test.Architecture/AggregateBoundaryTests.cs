namespace Test.Architecture;

/// <summary>
/// Enforces GR-15: aggregate-internal child entities are mutated only through their aggregate root.
/// An owned child (a 1:N owned entity or M:N junction with no life outside its root - e.g. Comment,
/// ChecklistItem, or the TaskItemTag junction on a TaskItem) must NOT expose a standalone
/// Create/Update/Delete CQRS command or handler, a transactional repository contract, or a write
/// method on its read service. Its writes flow through the root's Add*/Remove*/Transition domain
/// methods, the {Root}Updater graph sync, and nested sub-resource commands on the root
/// (AddTaskItemCommentCommand, RemoveTaskItemChecklistItemCommand, AssociateTaskItemTagCommand, ...).
/// Read queries (Search/GetById) on a child are allowed because read models may cross aggregate boundaries.
/// Pure-unit tier (reflection only): inspects loaded type metadata; no runtime, no infra.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public sealed class AggregateBoundaryTests : BaseTest
{
    // Entities classified as aggregate-internal children (GR-15). Independent aggregate roots
    // (TaskItem, Category, Tag) and polymorphic standalone entities owned by no single root
    // (Attachment, keyed by AttachmentOwnerType/OwnerId) are intentionally excluded - they keep
    // the full write slice. Add a child here when a new owned entity/junction is introduced.
    private static readonly Type[] OwnedChildEntities =
    [
        typeof(TaskFlow.Domain.Model.Comment),
        typeof(TaskFlow.Domain.Model.ChecklistItem),
        typeof(TaskFlow.Domain.Model.TaskItemTag),
    ];

    // Verb prefixes that denote a standalone child write (the anti-pattern). The legitimate
    // aggregate-routed commands are named Add/Update/Remove{Root}{Child}Command and never collide
    // with these exact {Prefix}{Child}Command / {Prefix}{Child}Handler names.
    private static readonly string[] WriteVerbPrefixes = ["Create", "Update", "Delete", "Upsert", "Patch"];

    /// <summary>Guards the owned-child list against rename/removal so the rule cannot silently pass on a typo.</summary>
    [TestMethod]
    public void OwnedChildEntities_AreRealDomainEntities()
    {
        var strays = OwnedChildEntities
            .Where(t => t.Assembly != DomainModelAssembly || !t.IsClass || t.IsAbstract)
            .Select(t => t.FullName)
            .ToList();

        Assert.IsEmpty(strays,
            $"Listed owned-child types are not concrete domain entities (fix the type or the OwnedChildEntities list): {string.Join(", ", strays)}");
    }

    /// <summary>Verifies owned children expose no standalone Create/Update/Delete CQRS command or handler (GR-15).</summary>
    [TestMethod]
    public void OwnedChildren_HaveNoStandaloneWriteCommandsOrHandlers()
    {
        var forbiddenNames = OwnedChildEntities
            .SelectMany(c => WriteVerbPrefixes
                .SelectMany(p => new[] { $"{p}{c.Name}Command", $"{p}{c.Name}Handler" }))
            .ToHashSet(StringComparer.Ordinal);

        var offenders = ApplicationCqrsAssembly.GetTypes()
            .Where(t => forbiddenNames.Contains(t.Name))
            .Select(t => t.FullName)
            .ToList();

        Assert.IsEmpty(offenders,
            "Owned children must not have standalone write commands/handlers - route writes through the aggregate root "
            + $"(e.g. AddTaskItem{nameof(TaskFlow.Domain.Model.Comment)}Command). Offenders: {string.Join(", ", offenders)}");
    }

    /// <summary>Verifies owned children expose no transactional repository contract - writes go through the root's repo (GR-15).</summary>
    [TestMethod]
    public void OwnedChildren_HaveNoTransactionalRepositoryContract()
    {
        // Persisting a child standalone requires an I{Child}RepositoryTrxn. Owned children expose only
        // I{Child}RepositoryQuery; their state changes are saved as part of the loaded aggregate root.
        var forbidden = OwnedChildEntities
            .Select(c => $"I{c.Name}RepositoryTrxn")
            .ToHashSet(StringComparer.Ordinal);

        var offenders = ApplicationContractsAssembly.GetTypes()
            .Where(t => t.IsInterface && forbidden.Contains(t.Name))
            .Select(t => t.FullName)
            .ToList();

        Assert.IsEmpty(offenders,
            "Owned children must not expose a transactional repository contract (GR-15); their writes flow through the root: "
            + string.Join(", ", offenders));
    }

    /// <summary>Verifies any owned-child service contract is read-only - no Create/Update/Delete write methods (GR-15).</summary>
    [TestMethod]
    public void OwnedChildren_ServiceContractsExposeReadsOnly()
    {
        HashSet<string> writeMethods = [with(StringComparer.Ordinal), "CreateAsync", "UpdateAsync", "DeleteAsync", "UpsertAsync"];
        var offenders = new List<string>();

        foreach (var child in OwnedChildEntities)
        {
            var contract = ApplicationContractsAssembly.GetTypes()
                .FirstOrDefault(t => t.IsInterface && t.Name == $"I{child.Name}Service");
            if (contract is null) continue; // a child with no service surface is fine

            offenders.AddRange(contract.GetMethods()
                .Where(m => writeMethods.Contains(m.Name))
                .Select(m => $"{contract.Name}.{m.Name}"));
        }

        Assert.IsEmpty(offenders,
            "Owned-child service contracts must be read-only (Search/Get only) per GR-15: " + string.Join(", ", offenders));
    }
}
