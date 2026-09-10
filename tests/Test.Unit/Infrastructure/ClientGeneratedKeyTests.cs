using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TaskFlow.Domain.Model;
using TaskFlow.Infrastructure.Data;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Pins the client-generated key assumption every TaskFlow entity depends on: ids are UUIDv7 values the
/// application mints (D-033 idempotent create, the caller-supplied create id, and the deterministic
/// per-iteration subtask id the decomposer workflow sends), so EF must treat <c>Id</c> as
/// application-assigned rather than store-generated.
/// <para>
/// EF Core's default for a Guid key is <c>ValueGeneratedOnAdd</c>. If
/// <c>EntityBaseConfiguration.Configure</c> ever stopped calling <c>ValueGeneratedNever()</c>, the
/// regression would be silent on insert and only show up as a child navigation-add being mistaken for an
/// UPDATE (see TaskItemUpdater) or as a caller-supplied id being discarded. This test makes it loud.
/// </para>
/// Pure-unit tier: model metadata only, built on the InMemory provider - no database.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ClientGeneratedKeyTests
{
    [TestMethod]
    public void EntityBaseConfiguration_LeavesEveryDomainEntityIdClientGenerated()
    {
        using var db = new TaskFlowDbContextTrxn(
            new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options)
        {
            AuditId = "client-generated-key-test",
            TenantId = TestConstants.TenantId
        };

        var domainEntities = db.Model.GetEntityTypes()
            .Where(entityType => IsTaskFlowEntity(entityType.ClrType))
            .ToList();

        Assert.IsGreaterThan(0, domainEntities.Count,
            "the model must map TaskFlowEntityBase-derived entities for this assertion to mean anything");

        foreach (var entityType in domainEntities)
        {
            var id = entityType.FindProperty("Id");
            Assert.IsNotNull(id, $"{entityType.ClrType.Name} must map an Id property");
            Assert.AreEqual(ValueGenerated.Never, id.ValueGenerated,
                $"{entityType.ClrType.Name}.Id must stay client-generated (EntityBaseConfiguration.ValueGeneratedNever); " +
                "EF Core's default for a Guid key is ValueGeneratedOnAdd, which discards the application's UUIDv7.");
        }
    }

    private static bool IsTaskFlowEntity(Type? clrType)
    {
        for (var type = clrType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(TaskFlowEntityBase<>))
                return true;
        }

        return false;
    }
}
