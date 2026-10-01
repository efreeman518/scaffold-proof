using EF.BackgroundServices.InternalMessageBus;
using EF.Common.Contracts;
using EF.Data.Contracts;
using EF.Data.Encryption;
using EF.Data.Interceptors;
using Microsoft.EntityFrameworkCore;
using Moq;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-023: the secure TaskItem columns are encrypted only by their value converter, so the CLR values the
/// audit interceptor serializes are plaintext. This drives the host's interceptor set on the InMemory
/// provider and asserts no audit payload - insert or update, current or original value - carries them.
/// Pure-unit tier (InMemory provider, mocked internal bus): the payload is built in-process at SavingChanges.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class AuditMaskingTests
{
    private const string DeterministicPlain = "det-plaintext-4711";
    private const string RandomPlain = "rnd-plaintext-0815";
    private const string DeterministicPlainUpdated = "det-plaintext-9001";
    private const string RandomPlainUpdated = "rnd-plaintext-9002";

    private readonly List<AuditEntry<string, Guid?>> _published = [];

    [TestMethod]
    public async Task Given_SecureTaskItemValues_When_CreatedAndUpdated_Then_AuditPayloadsCarryNoPlaintext()
    {
        var ct = TestContext.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var task = TaskItem.Create(
            TenantId.From(TestConstants.TenantId), "Audited task",
            secureDeterministic: DeterministicPlain, secureRandom: RandomPlain).Value!;

        await using (var db = Create(dbName))
        {
            db.TaskItems.Add(task);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        await using (var db = Create(dbName))
        {
            var loaded = await db.TaskItems.SingleAsync(t => t.Id == task.Id, ct);
            Assert.AreEqual(RandomPlain, loaded.SecureRandom, "the domain still sees the decrypted value");
            loaded.Update(secureDeterministic: DeterministicPlainUpdated, secureRandom: RandomPlainUpdated);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        var added = SingleTaskItemEntry("Added");
        var modified = SingleTaskItemEntry("Modified");
        StringAssert.Contains(modified.Metadata, nameof(TaskItem.SecureRandom),
            "the update changed the secure values, so the Modified payload must list them (masked)");

        foreach (var entry in new[] { added, modified })
        {
            foreach (var plaintext in new[] { DeterministicPlain, RandomPlain, DeterministicPlainUpdated, RandomPlainUpdated })
            {
                Assert.DoesNotContain(plaintext, entry.Metadata ?? string.Empty,
                    $"{entry.Action} audit payload leaks a secure value: {entry.Metadata}");
            }
        }
    }

    private AuditEntry<string, Guid?> SingleTaskItemEntry(string action) =>
        _published.Single(e => e.EntityType == nameof(TaskItem) && e.Action == action);

    private TaskFlowDbContextTrxn Create(string dbName)
    {
        var bus = new Mock<IInternalMessageBus>();
        bus.Setup(b => b.Publish(It.IsAny<InternalMessageBusProcessMode>(), It.IsAny<ICollection<AuditEntry<string, Guid?>>>()))
            .Callback<InternalMessageBusProcessMode, ICollection<AuditEntry<string, Guid?>>>((_, entries) => _published.AddRange(entries));

        // The host's write-context interceptor set (RegisterServices.Database), minus the outbox stager.
        return new(new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()
            .UseInMemoryDatabase(dbName)
            .UseColumnEncryption(TestColumnEncryption.Encryptor)
            .AddInterceptors(
                new AuditInterceptor<string, Guid?>(bus.Object, []),
                new BlindIndexInterceptor(TestColumnEncryption.Keys.BlindIndexKey))
            .Options)
        {
            AuditId = "audit-masking-test",
            TenantId = TestConstants.TenantId
        };
    }

    public TestContext TestContext { get; set; } = null!;
}
