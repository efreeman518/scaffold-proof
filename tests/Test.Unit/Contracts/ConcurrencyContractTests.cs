using EF.Common.Contracts;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Models;

namespace Test.Unit.Contracts;

/// <summary>
/// Unit coverage for the app's idempotent-create and save-failure primitives (D-032, D-033). The version guard,
/// the UUIDv7 rules and the 412/409 exception types are EF.Packages types with their own tests; what stays here
/// is the replay equivalence the app defines and the catch filter every save path uses.
/// Pure-unit tier: no DI, I/O, or host.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ConcurrencyContractTests
{
    /// <summary>Verifies replay equivalence compares scalars and ignores id, version, tenant, and children.</summary>
    [TestMethod]
    public void Given_TaskItemPayloads_When_Compared_Then_ScalarsDecideEquivalence()
    {
        var existing = new TaskItemDto
        {
            Id = Guid.CreateVersion7(),
            Version = 5,
            TenantId = Guid.NewGuid(),
            Title = "Same",
            Priority = TaskFlow.Domain.Shared.Enums.Priority.High,
            Comments = [new CommentDto { Body = "only on the stored copy" }]
        };

        var replay = new TaskItemDto
        {
            Id = Guid.CreateVersion7(),
            Version = null,
            TenantId = Guid.NewGuid(),
            Title = "Same",
            Priority = TaskFlow.Domain.Shared.Enums.Priority.High
        };

        Assert.IsTrue(IdempotentCreateGuard.IsEquivalent(existing, replay),
            "Id, Version, TenantId and children are deliberately excluded from the compare.");

        var divergent = replay with { Title = "Different" };
        Assert.IsFalse(IdempotentCreateGuard.IsEquivalent(existing, divergent));
    }

    /// <summary>Verifies category and tag replay equivalence over their own scalar sets.</summary>
    [TestMethod]
    public void Given_CategoryAndTagPayloads_When_Compared_Then_ScalarsDecideEquivalence()
    {
        var category = new CategoryDto { Name = "Ops", Description = "d", SortOrder = 2, IsActive = true };
        Assert.IsTrue(IdempotentCreateGuard.IsEquivalent(category, category with { Id = Guid.CreateVersion7(), Version = 9 }));
        Assert.IsFalse(IdempotentCreateGuard.IsEquivalent(category, category with { SortOrder = 3 }));

        var tag = new TagDto { Name = "urgent", Color = "#f00" };
        Assert.IsTrue(IdempotentCreateGuard.IsEquivalent(tag, tag with { Version = 4 }));
        Assert.IsFalse(IdempotentCreateGuard.IsEquivalent(tag, tag with { Color = "#0f0" }));
    }

    /// <summary>Verifies a divergent replay is a 409 conflict naming the entity and the caller's id.</summary>
    [TestMethod]
    public void Given_DivergentReplay_When_ReplayOrThrow_Then_ThrowsConflict()
    {
        var id = Guid.CreateVersion7();
        var tag = new TagDto { Name = "urgent", Color = "#f00" };

        var ex = Assert.ThrowsExactly<ConflictException>(() => IdempotentCreateGuard.ReplayOrThrow(
            tag, tag with { Color = "#0f0" }, IdempotentCreateGuard.IsEquivalent, "Tag", id));

        Assert.AreEqual("Tag", ex.EntityType);
        Assert.AreEqual(id.ToString(), ex.EntityId);
    }

    /// <summary>
    /// Verifies the save catch filter lets lost updates (412) and cancellations (499/504) through and converts
    /// only other failures into a failure Result.
    /// </summary>
    [TestMethod]
    public void Given_SaveExceptions_When_Filtered_Then_OnlyOtherFailuresMapToResult()
    {
        Assert.IsFalse(SaveFailure.MapsToFailureResult(new PreconditionFailedException("TaskItem", "id", 1, 2)));
        Assert.IsFalse(SaveFailure.MapsToFailureResult(new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException()));
        Assert.IsFalse(SaveFailure.MapsToFailureResult(new OperationCanceledException()));
        Assert.IsTrue(SaveFailure.MapsToFailureResult(new InvalidOperationException()));
    }
}
