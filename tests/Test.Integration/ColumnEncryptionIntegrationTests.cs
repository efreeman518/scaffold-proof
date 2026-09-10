using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Text;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data.Encryption;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;

namespace Test.Integration;

/// <summary>
/// D-023 on a real database: secure columns are stored as AES-GCM ciphertext on both providers, decrypt on
/// read, and SecureDeterministic is equality-queryable only through its blind index. Also proves the app-managed
/// Version token round-trips through the provider (D-021).
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class ColumnEncryptionIntegrationTests
{
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("Database", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task SecureColumns_AreEncryptedAtRest_AndQueryableByBlindIndex()
    {
        var token = $"token-{Guid.NewGuid():N}";
        const string secret = "top secret note";
        Guid taskId;

        await using (var db = DbContainerFixture.CreateTrxnContext())
        {
            await db.Database.MigrateAsync(TestContext.CancellationToken);
            var task = TaskItem.Create(
                DomainId.From<TenantId>(TestConstants.TenantId), $"Encrypted {token}",
                secureDeterministic: token, secureRandom: secret).Value!;
            db.TaskItems.Add(task);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
            taskId = task.Id;
            Assert.AreEqual(1, task.Version);
        }

        // Raw bytes (standard SQL, quoted identifiers, bypassing the model converters): neither column may
        // contain the UTF-8 plaintext; the blind index is a 32-byte HMAC.
        await using (var db = DbContainerFixture.CreateQueryContext())
        {
            var deterministic = await db.Database
                .SqlQuery<byte[]>($"""SELECT "SecureDeterministic" AS "Value" FROM taskflow."TaskItem" WHERE "Id" = {taskId}""")
                .SingleAsync(TestContext.CancellationToken);
            var random = await db.Database
                .SqlQuery<byte[]>($"""SELECT "SecureRandom" AS "Value" FROM taskflow."TaskItem" WHERE "Id" = {taskId}""")
                .SingleAsync(TestContext.CancellationToken);
            var blindIndex = await db.Database
                .SqlQuery<byte[]>($"""SELECT "SecureDeterministicBlindIndex" AS "Value" FROM taskflow."TaskItem" WHERE "Id" = {taskId}""")
                .SingleAsync(TestContext.CancellationToken);

            CollectionAssert.AreNotEqual(Encoding.UTF8.GetBytes(token), deterministic);
            CollectionAssert.AreNotEqual(Encoding.UTF8.GetBytes(secret), random);
            Assert.AreEqual(AesGcmColumnEncryptor.NonceSizeBytes + Encoding.UTF8.GetByteCount(token) + AesGcmColumnEncryptor.TagSizeBytes, deterministic.Length);
            Assert.HasCount(BlindIndex.SizeBytes, blindIndex);
        }

        // Decrypted through the model, and found through the blind index.
        await using (var db = DbContainerFixture.CreateQueryContext())
        {
            var repo = new TaskItemRepositoryQuery(db, TestColumnEncryption.Keys, TestCursorCodec.Instance);
            var found = await repo.FindBySecureTokenAsync(token, TestContext.CancellationToken);

            Assert.IsNotNull(found);
            Assert.AreEqual(taskId, found.Id.Value);
            Assert.AreEqual(token, found.SecureDeterministic);
            Assert.AreEqual(secret, found.SecureRandom);
            Assert.IsNull(await repo.FindBySecureTokenAsync("no-such-token", TestContext.CancellationToken));
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Version_IncrementsOnUpdate_AndRejectsStaleWriter()
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(TestContext.CancellationToken);
        var task = TaskItem.Create(DomainId.From<TenantId>(TestConstants.TenantId), $"Versioned {Guid.NewGuid():N}").Value!;
        db.TaskItems.Add(task);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);

        await using var stale = DbContainerFixture.CreateTrxnContext();
        var staleCopy = await stale.TaskItems.SingleAsync(t => t.Id == task.Id, TestContext.CancellationToken);

        task.Update(title: "first writer");
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(2, task.Version);

        // Throw (not the package ClientWins retry) so the conflict surfaces to the caller.
        staleCopy.Update(title: "stale writer");
        await Assert.ThrowsExactlyAsync<DbUpdateConcurrencyException>(
            () => stale.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken));
    }

    public TestContext TestContext { get; set; } = null!;
}
