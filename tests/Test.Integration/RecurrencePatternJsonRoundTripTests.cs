using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Pins the stored JSON document of <see cref="TaskItem.RecurrencePattern"/> on the selected provider lane, read as
/// raw column text, and its round trip. The column is a JSON document (jsonb on PostgreSQL, json on SQL Server) and
/// rows written by an earlier build must keep reading, so a change of the EF mapping (owned type to complex type) or
/// of <see cref="RecurrencePattern"/> must not change what is stored: a set pattern is one object with the three
/// members, no pattern is a database NULL. Component tier.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class RecurrencePatternJsonRoundTripTests
{
    /// <summary>Applies migrations once for the class.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError)) return;
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Inconclusive without a container runtime; fails when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    /// <summary>A set pattern is stored as the pinned document and reads back equal; no pattern is stored as NULL.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task RecurrencePattern_IsStoredAsThePinnedDocument_AndNullWhenAbsent()
    {
        var ct = TestContext.CancellationToken;
        var tenantId = TenantId.From(Guid.NewGuid());
        var now = new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var withPattern = TaskItem.Create(tenantId, "Recurring").GetValueOrThrow();
        withPattern.UpdateRecurrencePattern(
            new RecurrencePattern { Frequency = RecurrencePattern.Weekly, Interval = 2, EndDate = now.AddDays(30) }, now);
        var withoutPattern = TaskItem.Create(tenantId, "One-off").GetValueOrThrow();

        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            seed.TaskItems.AddRange(withPattern, withoutPattern);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
        }

        await using var query = DbContainerFixture.CreateQueryContext();
        // Compared as members, not text: PostgreSQL jsonb hands back its own normalized text (keys ordered by
        // length, a space after each separator), so member order is not a stored property there.
        using (var document = JsonDocument.Parse(await StoredDocumentAsync(query, withPattern.Id.Value, ct)
            ?? throw new AssertFailedException("A set pattern was stored as NULL.")))
        {
            var members = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
            CollectionAssert.AreEquivalent(
                new Dictionary<string, string>
                {
                    ["EndDate"] = "\"2030-06-05T07:08:09+00:00\"",
                    ["Interval"] = "2",
                    ["Frequency"] = "\"Weekly\""
                },
                members,
                "The stored RecurrencePattern document changed shape; rows written by an earlier build would not read the same: "
                + document.RootElement.GetRawText());
        }

        Assert.IsNull(await StoredDocumentAsync(query, withoutPattern.Id.Value, ct), "No pattern must be a database NULL.");

        var stored = await query.TaskItems.AsNoTracking().SingleAsync(t => t.Id == withPattern.Id, ct);
        Assert.IsNotNull(stored.RecurrencePattern);
        Assert.AreEqual(RecurrencePattern.Weekly, stored.RecurrencePattern.Frequency);
        Assert.AreEqual(2, stored.RecurrencePattern.Interval);
        Assert.AreEqual(now.AddDays(30), stored.RecurrencePattern.EndDate);
        Assert.IsNull((await query.TaskItems.AsNoTracking().SingleAsync(t => t.Id == withoutPattern.Id, ct)).RecurrencePattern);
    }

    /// <summary>The raw column text; the cast is the only provider-specific part ({0} is the id parameter).</summary>
    private static async Task<string?> StoredDocumentAsync(DbContext db, Guid taskId, CancellationToken ct)
    {
        var sql = DbContainerFixture.Provider == TaskFlowDbProvider.PostgreSql
            ? """SELECT CAST("RecurrencePattern" AS text) AS "Value" FROM taskflow."TaskItem" WHERE "Id" = {0}"""
            : """SELECT CAST("RecurrencePattern" AS nvarchar(max)) AS "Value" FROM taskflow."TaskItem" WHERE "Id" = {0}""";
        return await db.Database.SqlQueryRaw<string?>(sql, taskId).SingleAsync(ct);
    }

    public TestContext TestContext { get; set; } = null!;
}
