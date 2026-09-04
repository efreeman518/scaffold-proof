# EF.* Package Change Requests - hand-off to the package coding agent

Source: docs/plans/scale-baseline-dual-provider-plan.md (TaskFlow scalability baseline). Consumer: scaffold-proof (TaskFlow). Current pins: EF.* 1.0.95 / 1.0.88, EF.FlowEngine.* 1.0.162; targets after Phase 0: 1.1.98 / 1.0.163. Re-verify each item against the bumped versions before implementing; several may already exist.

## EF.* package change requests (hand-off to the package coding agent)

Analyzed from cached 1.0.95 / 1.0.162 contracts; re-verify each against 1.1.98 / 1.0.163 before sending. Each: package, member, rationale, acceptance. App-local fallbacks noted where the repo can proceed without the package.

REQUIRED (blocks this repo):
1. EF.Domain `EntityBase<TId>`: replace `byte[]? RowVersion` with `long Version { get; set; }`. Acceptance: new entity `Version == 0`; `RowVersion` no longer compiles.
2. EF.Data `DbContextBase<,>.SaveChangesAsync`: for every `Modified` entry whose entity is `EntityBase<>`, increment `Version` and set `OriginalValue` to the pre-increment value. Acceptance: modify+save twice -> 2; stale copy save throws `DbUpdateConcurrencyException`. (Fallback: app-local `VersionInterceptor` in Infrastructure.Data, < 40 lines.)
3. EF.Data composition: remove `Microsoft.EntityFrameworkCore.SqlServer` and `Microsoft.Data.SqlClient.AlwaysEncrypted.AzureKeyVaultProvider` from the nuspec; move `MigrationSupport` and `RepositoryBase.SetNoLock/SetLock` to new `EF.Data.SqlServer`. Acceptance: a project referencing EF.Data + Npgsql restores with no `Microsoft.Data.SqlClient` in the lock file.
4. EF.Data interceptors: gate `ConnectionNoLockInterceptor`/`ReadUncommittedInterceptor` on SQL Server; replace `bool readNoLock` in `IRepositoryBase.QueryPage*` with `enum ReadIsolation { Default, ReadUncommitted }` (keep `[Obsolete]` overload one release). Acceptance: Npgsql connection emits no `SET TRANSACTION ISOLATION LEVEL`.
5. New EF.Data.Encryption (provider-neutral): `IColumnEncryptor { byte[] Encrypt(string); string Decrypt(byte[]); ValueConverter<string,byte[]> StringConverter }`, `AesGcmColumnEncryptor` (nonce 12 || ciphertext || tag 16), `BlindIndex.Compute(value, key)` HMAC-SHA256, `PropertyBuilder<string?>.HasBlindIndex(shadowName)`, `BlindIndexInterceptor : ISaveChangesInterceptor`, `KeyVaultDekProvider` (RSA-OAEP unwrap, cached), `ColumnEncryptionOptions`, `services.AddColumnEncryption(config, "Database:Encryption")`, and `DbContextOptionsBuilder.UseColumnEncryption(IColumnEncryptor)` (options extension so pooled contexts can resolve the encryptor in `OnModelCreating`). Acceptance: round-trip equality, two encryptions differ, blind index stable.
6. EF.Data.Contracts keyset paging: `KeysetPageAsync<T,TKey>(IQueryable<T>, keySelector, tieBreaker, cursor, pageSize, descending, ct)`, `record CursorPage<T>(Items, NextCursor, HasMore)`, `CursorCodec` (versioned, HMAC-signed base64url). Acceptance: paging 1000 rows by 100 during concurrent inserts returns each id once. (Fallback: app-local LINQ `(k > @k) || (k == @k && Id > @id)` in `TaskItemRepositoryQuery`.)
7. EF.Data `IRepositoryBase.UpsertAsync` backed by FlexLabs.EntityFrameworkCore.Upsert (`On(...).NoUpdate()/WhenMatched(...)`), dependency in nuspec. Acceptance: two concurrent upserts of one key leave one row on both providers.
8. EF.IntegrationTesting: `PostgreSqlContainerFixture` (default image `pgvector/pgvector:pg17`) and `DbContextOptionsFactory.BuildNpgsqlOptions<T>`; nuspec adds `Testcontainers.PostgreSql`, `Npgsql.EntityFrameworkCore.PostgreSQL`. Acceptance: fixture starts, `CanConnectAsync` true.
9. EF.FlowEngine.Outbox.Sql / CircuitBreaker.Sql: remove `[Column(TypeName="nvarchar(max)")]` from `FlowEngineOutboxRow.Payload` and `FlowEngineCircuitBreakerRow.FailureTimestampsJson`; verify the SQL store family scaffolds and round-trips on Npgsql. Acceptance: Npgsql migration emits `text`; save+dispatch passes on Postgres. (Interim shim in `TaskFlowFlowEngineDbContext` until published.)
10. EF.Data.Contracts batch helpers: `ExecuteUpdateBatchedAsync<T>(filter, set, batchSize=1000, maxBatches=100)` and `ExecuteDeleteBatchedAsync`. Acceptance: 2500 rows, batch 1000 -> 3 statements, returns 2500. (Fallback: app-local loop in `TaskItemSystemRepository`.)
11. EF.BackgroundServices: `AddLeasedWorkerService<TWorker,TOptions>()` + `LeasedWorkerBase.ProcessBatchAsync(leaseToken, ct)` with `LeasedWorkerOptions {PollInterval, IdleBackoffMax, LeaseDuration, BatchSize, MaxAttempts, JitterFactor}`. Acceptance: empty batches back off 1s -> 5s within 5 iterations, reset on first non-empty, stop within one poll of cancellation. (Fallback: app-local base class shared by the two Scheduler workers.)

NICE-TO-HAVE:
12. EF.Common `DeterministicGuid.Create(ns, parts)` (UUIDv5, correct version/variant bits). Fallback ~20 lines app-local.
13. EF.Cache `ITypedCache` with tagging/profiles/degraded events over FusionCache (`EF.Cache` already owns `CacheSettings`).
14. EF.Messaging `ServiceBusSenderPool`, `IntegrationEventEnvelope`, `EnvelopeSerializer`.
15. EF.Table `DeleteByPartitionRangeAsync(prefix, cutoff, batchSize)` for audit retention.
16. EF.IntegrationTesting `EfWebApplicationFactoryBase.BuildOptionsFor<TContext>(connectionString)` virtual.
17. EF.FlowEngine: split `Clients.Sql.AdHocSqlQueryClient` into `Clients.SqlServer`; README guidance for `UseRetentionPolicy`.
18. REQUIRED: EF.FlowEngine / Clients.Http: confirm or add per-node `config.headers` (`Dictionary<string,string>`, templated) on integration nodes (symbols `CustomHeaders/BuildHeaders/configHeaders` exist in 1.0.162, undocumented). Without it no workflow can perform a conditional write.
19. EF.FlowEngine: `newGuidV7()` expression function (or engine-stable per-iteration ids) so loop-created entities are idempotent.
20. EF.AspNetCore: `ProblemDetailsHelper` overloads for 412/428/409 and reusable `IfMatchEndpointFilter`/`ETagEndpointFilter`.
21. EF.Common.Contracts: promote `CursorPage<T>`/`CursorSearchRequest<TFilter,TSortMode>`/`PageSizeLimits` next to `SearchRequest`/`PagedResponse` after the shape settles (keep app-local this release).
22. EF.Data.Contracts: `WherePropertyIn` overload over a navigation collection (`Any(Contains)` shape).


23. REQUIRED-LATER: new `EF.Messaging.RabbitMq` (connection multiplexer with publisher-confirm channel pool, topology declaration, consumer hosted service with per-queue prefetch and x-death based DLX) extracted from `TaskFlow.Infrastructure.Messaging.RabbitMq` once the shape settles in scaffold-proof.

24. NICE-TO-HAVE: EF.Messaging.RabbitMq - make `RabbitMqHeaders.AsString` (UTF-8 header decoding) public; every consumer currently re-implements it.
