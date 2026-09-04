using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Models;

namespace Test.Unit.Contracts;

/// <summary>
/// Unit coverage for the concurrency and idempotent-create primitives (D-032, D-033). These are pure
/// functions with no dependencies, so a mistake in them is cheapest to catch here - and most expensive
/// to catch in production, where it shows up as a silently lost update or a duplicated create.
/// Pure-unit tier: no DI, I/O, or host.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ConcurrencyContractTests
{
    /// <summary>Verifies a matching expected version passes the guard.</summary>
    [TestMethod]
    public void Given_MatchingVersion_When_Require_Then_DoesNotThrow()
    {
        ConcurrencyGuard.Require(expected: 7, current: 7, "TaskItem", Guid.CreateVersion7());
    }

    /// <summary>Verifies a stale expected version throws with both versions attached for the 412 response.</summary>
    [TestMethod]
    public void Given_StaleVersion_When_Require_Then_ThrowsWithCurrentVersion()
    {
        var id = Guid.CreateVersion7();

        var ex = Assert.ThrowsExactly<ConcurrencyMismatchException>(
            () => ConcurrencyGuard.Require(expected: 3, current: 9, "TaskItem", id));

        Assert.AreEqual("TaskItem", ex.EntityType);
        Assert.AreEqual(id, ex.EntityId);
        Assert.AreEqual(3, ex.Expected);
        Assert.AreEqual(9, ex.Current);
    }

    /// <summary>Verifies the wildcard (If-Match: *) skips the precondition entirely.</summary>
    [TestMethod]
    public void Given_WildcardExpectation_When_Require_Then_DoesNotThrow()
    {
        ConcurrencyGuard.Require(expected: null, current: 42, "TaskItem", Guid.CreateVersion7());
    }

    /// <summary>
    /// Verifies the mismatch exception does not derive from InvalidOperationException, which the global
    /// handler maps to 400 - inheriting it would silently downgrade every 412 to a bad request.
    /// </summary>
    [TestMethod]
    public void Given_ConcurrencyMismatchException_When_Inspected_Then_IsNotInvalidOperation()
    {
        Assert.IsNotInstanceOfType<InvalidOperationException>(
            new ConcurrencyMismatchException("TaskItem", Guid.CreateVersion7(), 1, 2));
    }

    /// <summary>Verifies a v7 id is accepted and a v4 id is rejected (GR-17).</summary>
    [TestMethod]
    public void Given_CallerIds_When_Validated_Then_OnlyUuidV7Passes()
    {
        Assert.IsTrue(UuidV7.IsV7(Guid.CreateVersion7()));
        Assert.IsFalse(UuidV7.IsV7(Guid.NewGuid()));

        Assert.IsTrue(UuidV7.ValidateCallerId(null).IsSuccess, "An absent id is valid: the server generates one.");
        Assert.IsTrue(UuidV7.ValidateCallerId(Guid.Empty).IsSuccess, "Guid.Empty means 'unset', not 'bad v4'.");
        Assert.IsTrue(UuidV7.ValidateCallerId(Guid.CreateVersion7()).IsSuccess);
        Assert.IsTrue(UuidV7.ValidateCallerId(Guid.NewGuid()).IsFailure);
    }

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

    /// <summary>Verifies a non-concurrency exception is not mistaken for a lost update by the catch filters.</summary>
    [TestMethod]
    public void Given_Exceptions_When_Classified_Then_OnlyDbUpdateConcurrencyIsAConcurrencyFailure()
    {
        Assert.IsFalse(ConcurrencyGuard.IsConcurrencyFailure(new InvalidOperationException()));
        Assert.IsFalse(ConcurrencyGuard.IsConcurrencyFailure(
            new ConcurrencyMismatchException("TaskItem", Guid.CreateVersion7(), 1, 2)));
        Assert.IsTrue(ConcurrencyGuard.IsConcurrencyFailure(
            new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException()));
    }
}
