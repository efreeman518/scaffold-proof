# REFERENCE-STATUS - TaskFlow

Canonical current evidence for the TaskFlow reference application. Historical phase narrative belongs in Git history; root `HANDOFF.md` contains only terminal routing state.

> Update this file only from observed results. TaskFlow CI records the scaffold checkout commit used for cross-repository validation so failures remain diagnosable without creating a compatibility pin.

This refresh follows the `feature/ef-packages-1-1-100` package-refactor: EF.* pinned at 1.1.102, EF.FlowEngine.*/EF.FilterBuilder at 1.0.173, app-local fallbacks for the landed package requests deleted. Numbers below include the observed local fast lane and AppHost build on 2026-09-12.

## Build Status

| Field | Value |
|---|---|
| Last verified | 2026-09-12 (local fast lane and AppHost build) |
| Solution | `TaskFlow.slnx` |
| Target framework | .NET 10 |
| Configuration | Release |
| Solution projects declared (`dotnet sln list` / slnx `<Project Path=` count) | 47 (two projects deleted this refactor: `src/Packages/EF.Messaging.RabbitMq`, `tests/EF.Messaging.RabbitMq.Tests`) |
| Solution build units (`dotnet build TaskFlow.slnx -c Release -m:1` summary line) | 50 |
| Errors | 0 |
| Warnings | 0 |

`src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj` builds separately (Release, 3 projects: `TaskFlow.Uno`, `TaskFlow.Uno.Core`, `TaskFlow.Uno.Presentation`) because the Uno SDK requires explicit invocation; 0 errors, 0 warnings.

`dotnet ef migrations has-pending-model-changes` is clean for all 6 context/provider pairs (the app `DbContext` and the FlowEngine `DbContext`, each against SqlServer and PostgreSql, plus the TickerQ context pairing where applicable).


## Test Status

Numbers below are the observed local fast lane on 2026-09-12.

**Fast lane** (`TestCategory=Unit|TestCategory=Architecture|TestCategory=Endpoint`, `dotnet test TaskFlow.slnx`): **653 passed, 0 failed, 0 skipped, across 13 projects.**

Fast-lane delta vs the 664-passed baseline recorded before this refactor's wave 2: +1 (E2 cross-version cursor test) - 15 (deleted `EF.Messaging.RabbitMq.Tests` unit tests, project removed) + 3 (E3 architecture tests: cached types public, etc.) - 4 (deleted `FlowEngineIfMatchOverrideHandler` tests, D-032 superseded) + 1 (A2, `ClientGeneratedKeyTests` pinning `ValueGenerated.Never`) + 1 (`UuidV7.TimestampOf` case) = 664 - 13 = **651**. Per-project breakdown for the individual fast-lane projects was not independently recomputed this pass; the total above is the verified figure.

Docker/Testcontainers-backed lanes (run-scoped `TESTCONTAINERS_HOST_OVERRIDE`, not committed):

| Project | Category filter | Verified count | Notes |
|---|---|---:|---|
| Test.Integration | `TestCategory=Integration` | 63 (SqlServer) / 65 (PostgreSql) | Includes the relational read-model, relational audit, S3 storage, distributed-lock, MessagePack cache, pgvector (PostgreSql only), and keyset/projection round-trip coverage carried over from the prior refresh, plus the cross-version cursor-token compatibility case (a token minted by EF.Data.Contracts 1.1.101 decodes and resumes under 1.1.102) |
| Test.E2E | `TestCategory=E2E` | 10 (SqlServer) / 10 (PostgreSql) | Unchanged shape |
| Test.Integration.FlowEngine | `TestCategory=Integration` | 18 (SqlServer) / 18 (PostgreSql) | Includes the workflow-definition test pinning the absence of a UUIDv7 loop-iteration id (request 19 rejection) |
| Test.UI | `UI`, `Presentation` | 56 | Headless UI and presentation contracts |
| Vulnerability audit | `dotnet list TaskFlow.slnx package --vulnerable --include-transitive` | 47 projects, 0 with vulnerable packages | Per-project audit over the full solution |

`EF.Messaging.RabbitMq.Tests` (both the unit and Testcontainers.RabbitMq integration lanes, 31 tests total) no longer exists in this repo: the package shipped and the in-repo project was deleted (request 23; see `docs/plans/ef-messaging-rabbitmq-package-spec.md`). Its coverage now lives in the published `EF.Messaging.RabbitMq` package's own test suite, outside this repo.

Not rerun this pass, last observed values only: **Test.Mutation** last observed 33 (mutation-target contract tests; not part of this refactor's changed surface, not rerun to save time).

Not fully rerun on this machine this pass: **Test.Aspire** (full graph), **Test.Mobile** (dedicated Appium/emulator runner), **Test.FoundryLocal** (live local-model lane, RID-bound runtime), **Test.Load** (manual; the 5,000 RPS gate is deployment-only), **Test.Benchmarks** (BenchmarkDotNet console runner, build-verified only), **compose-smoke** and **deploy-vps** (CI-only: `compose-smoke` is a `workflow_dispatch`-gated job in `ci.yml`, `deploy-vps.yml` is `workflow_dispatch`-only). Mirrored WSL networking now permits Aspire/DCP localhost publishing on this machine; the focused React Playwright suite passed after the AppHost and gateway test-configuration fixes, while a later retry encountered transient gateway startup failure before browser execution.

Published Release Uno cold-start and normal browser projects pass from empty browser state without refresh, retry, sleep, or exception suppression. Browser WASM Release temporarily sets `PublishTrimmed=false` because the current Navigation, Toolkit, and WinUI package set emits upstream `IL2104` under warnings-as-errors. Removal condition: those packages become trim-clean. Not re-verified in this pass (no Uno-affecting code changed in this refactor).

## Vulnerability Status

Run `dotnet list package --vulnerable --include-transitive` and capture findings here. Severity policy: [scaffold execution gates](https://github.com/efreeman518/scaffold-ai/blob/main/support/execution-gates.md#vulnerability-audit).

Last audit (2026-09-10, orchestrator's gate on the merged tree, detached worktree): `dotnet list TaskFlow.slnx package --vulnerable --include-transitive` over 47 projects reported no vulnerable packages or advisories, direct or transitive, for any project.

| Package | Severity | Direct/Transitive | Advisory | Notes |
|---|---|---|---|---|
| _None_ | - | - | - | Full solution audit reported no vulnerable packages |

## Capability Coverage

Status meanings:

- `proven`: implemented and covered by executable build, test, or smoke evidence.
- `deployment-only`: generated or wired, but live acceptance requires deployed external resources or identity.
- `CI-only`: exercised by CI (typically `workflow_dispatch`-gated), not runnable on this dev machine, but not deployment-only either.
- `documented-only`: example or opt-in documentation exists without active runtime wiring.
- `not enabled`: intentionally absent from the TaskFlow configuration.

### Pre-existing capabilities (carried forward from the prior refresh, unaffected by this refactor)

| Capability | Status | Evidence boundary |
|---|---|---|
| Service and CQRS application-style switch | proven | Shared Endpoint and E2E suites run both styles on both DB providers; `ApplicationStyleResolver` owns selection |
| Dual EF Core provider (SQL Server + PostgreSQL) | proven | `TaskFlowDbProviderSelector`/`UseTaskFlowProvider`; Unit, Integration, E2E, and migrator contracts run once per `TASKFLOW_TEST_DB_PROVIDER` value |
| Composite tenant-first PK, app-managed Version/ETag, migrations | proven | `EntityBaseConfiguration` composite `(TenantId, Id)` key; `VersionTimestampInterceptor`; two migration assemblies |
| ETag / If-Match / 412 / 428 concurrency | proven | `ConcurrencyGuard`, `IfMatchEndpointFilter`, `ETagEndpointFilter`; Endpoint and E2E cases |
| Caller-supplied UUIDv7 idempotent create | proven | `UuidV7`/`IdempotentCreateGuard`; Endpoint cases cover non-v7 400, equivalent replay 200, divergent 409 |
| Cursor paging (task items) | proven | `CursorSearchRequest`/`CursorPage`/`ICursorProtector`; Endpoint/E2E cases |
| App-layer column encryption + blind index | proven | `Infrastructure.Data/Encryption/*` on both providers; Always Encrypted (D-019) kept documented-only |
| Transactional outbox + consumer inbox | proven | `OutboxStagingInterceptor`, lease claim, `OutboxDispatcherService`; `ConsumerInbox`/`IInboxStore.TryClaimAsync` |
| Messaging transport switch (Service Bus / RabbitMQ) | proven | `Messaging:Provider`; `EF.Messaging.RabbitMq` (published package, own test suite outside this repo) + adapter tests |
| Redis cache (FusionCache) and rate limiter | proven | `EF.Cache.ITypedCache`/`CacheSettings` injected directly (app-local `ITaskFlowCache`/`FusionTaskFlowCache` deleted); `FailOpenRateLimiter` over `RedisRateLimiting` |
| Generated API clients (Refitter, openapi-typescript) | proven | `src/UI/TaskFlow.ApiClient` and React `types.ts` regenerate from the committed OpenAPI document |
| Aspire, Gateway, Scheduler, Functions | proven (mesh unverified here) | Build, topology, unit, endpoint coverage; the DCP-dependent Aspire mesh lane cannot run on this machine |
| Uno, Blazor, React | proven | Build, Test.UI, and dedicated mobile evidence; full-stack Playwright blocked here by the same Aspire limitation |
| FlowEngine | proven | Runtime wiring, separate-schema migration, definition/integration cases including the If-Match:* connector override (D-032) |
| Foundry Local inference | proven (not rerun this pass) | Dedicated live lane requires the RID-bound Foundry Local runtime |
| GitHub Actions and deployment workflow shape | proven | Workflow contract tests and CI execution |
| Bicep module shape (incl. Postgres, Redis, RabbitMQ container app, scale rules) | proven | `az bicep build` and Bicep contract tests |
| Live Entra or CIAM sign-in | deployment-only | Scaffold auth is the local proof |
| Azure Foundry and Azure AI Search | deployment-only | Provider wiring and gated smoke tests exist; live resources not required locally |
| Key Vault backed encryption and data-protection keys | deployment-only | AppHost and Bicep wiring exist; live vault, CMK, identity, RBAC require deployment |
| 5,000 RPS load gate | deployment-only | Test.Load exists (NBomber) but is manual |
| Production infrastructure rollout | deployment-only | Deployment workflow and Bicep validated without a live rollout |
| Existing Foundry account, prompt agent, pre-existing agent opt-ins | documented-only | Commented examples only |
| Notifications | not enabled | `includeNotifications: false` |
| `azd` orchestration | not enabled | `includeAzd: false` |
| Private endpoints | not enabled | `usePrivateEndpoints: false` |

### New capabilities added by this refactor (Portable lane + scale-guidance alignment)

| Capability | Status | Evidence boundary |
|---|---|---|
| Hosting-lane preset (`TASKFLOW_LANE=Azure\|Portable`, D-035) | proven | `HostingLaneSelector`; `Test.Unit/Hosting/ProviderSwitchSelectorTests.cs`; `Test.Aspire/AppHostLaneTopologyTests.cs` (7 Portable-topology assertions, part of the 12 non-DCP Test.Aspire tests) |
| S3 object storage (D-037) | proven | `Test.Integration/S3ObjectStorageRepositoryTests.cs` (MinIO Testcontainers); `Test.Unit/Infrastructure/S3StorageRegistrationTests.cs` |
| Relational read model (D-038) | proven | `Test.Integration/RelationalTaskViewRepositoryTests.cs`, both DB lanes |
| Relational audit sink (D-039) | proven | `Test.Integration/RelationalAuditLogRepositoryTests.cs`, both DB lanes |
| pgvector semantic search (D-040) | proven | `Test.Integration/PgVectorSearchTests.cs` (PostgreSql lane only; fails fast at startup on SqlServer by design) |
| OpenAI-compatible LLM client (D-041) | proven | `Test.Unit/AI/AiProviderSelectorTests.cs` (fail-fast on missing endpoint/API key, registers both `IChatClient` and the embedding generator) |
| App Configuration + dynamic feature flags (D-042) | proven | `Test.Architecture/FeatureManagementArchitectureTests.cs`; `Test.Endpoints/FeatureFlagEndpointTests.cs` (off -> 404, on -> 200, boots without `AppConfig:Endpoint`); `Test.Unit/Hosting/TenantTargetingContextAccessorTests.cs`. Not wired into the Functions host (open item) |
| Data Protection Redis persistence (D-043) | proven (selector only) | `Test.Unit/Hosting/ProviderSwitchSelectorTests.cs` proves the switch resolves and falls back correctly; actual cross-replica key-ring persistence behavior under Redis is not separately integration-tested |
| Postgres pooler mode (D-045) | proven | `Test.Unit/Infrastructure/TaskFlowDbProviderSelectorTests.cs` (`PoolerModeSelector`, connection-string flag appending) |
| Runtime profile per host (D-047) | proven | `Test.Architecture/HostRuntimeSettingsTests.cs` (every host csproj imports `TaskFlow.Host.props`) |
| Source-generated JSON contexts (D-048) | proven | `Test.Architecture/JsonContextCompletenessTests.cs`; `Test.UI/Uno/TaskFlowApiJsonContextTests.cs` |
| Health probe contract (D-049) | proven | `Test.Endpoints/HealthProbeContractTests.cs`; `Test.Unit/Gateway/GatewayHealthCheckRegistrationTests.cs` |
| Edge rate limiter + YARP active/passive health (D-050) | proven | `Test.Unit/Gateway/GatewayEdgeRateLimitTests.cs` (burst over the token bucket -> 429) |
| GET-only hedging (D-051) | proven | `Test.Unit/Hosting/ReadHedgingTests.cs` (GET hedges, POST never does); `Test.Unit/Infrastructure/CosmosHedgingOptionsTests.cs` (config-gated, deployment-only for the live behavior) |
| Distributed lock (D-052) | proven | `Test.Integration/RedisDistributedLockTests.cs` (two contenders, one wins, second wins after release); `Test.Unit/Infrastructure/InProcessDistributedLockTests.cs` (fallback) |
| Broker trace propagation (D-053) | proven | `Test.Unit/Infrastructure/BrokerTracePropagationTests.cs` (`ActivityListener` asserts a consumed message with an injected `traceparent` yields a linked Consumer activity) |
| LoggerMessage sweep + CA1848 (D-053) | proven | `src/.editorconfig` (`dotnet_diagnostic.CA1848.severity=error` for `src/**.cs`); full solution build 0 warnings/errors after all 58 raw call sites converted |
| Internal gRPC read service (D-054) | proven | `Test.Endpoints/TaskFlowReadGrpcTests.cs` (in-memory `GrpcChannel` parity with the REST summary); `Test.Unit/Contracts/TaskFlowReadGrpcMapperTests.cs`; `Test.Architecture/GrpcArchitectureTests.cs` (gRPC service lives only in the Api host) |
| MessagePack L2 cache serializer (D-048/D-056) | proven | `Test.Unit/Infrastructure/CacheSerializerTests.cs` (round trip, both serializers); `Test.Integration/MessagePackCacheTests.cs` (L2 Redis round trip) |
| Compose lane (Docker Compose + Caddy) + VPS deploy workflow (D-036) | CI-only | `docker compose config -q` on every `ci.yml` run; dispatch-gated `compose-smoke` job (local-profile stack, `/healthz/ready` through Caddy + one CRUD round trip); `deploy-vps.yml` (`workflow_dispatch` deploy/rollback). Nothing in the compose/VPS path has executed on this dev machine - CI's `compose-smoke` run is the first real execution |

The declared flags and matrix must agree with `.scaffold/resource-implementation.yaml`. Proof paths are validated against the scaffold-owned [TaskFlow proof map](https://github.com/efreeman518/scaffold-ai/blob/main/support/taskflow-proof-map.md).

## Phase Completion

Phases 1 through 5e and the FlowEngine extension are complete. Root `HANDOFF.md` records `workflowStatus: complete`, `currentPhase: 5`, and `currentSubPhase: complete`. The Portable-lane + scale-guidance-alignment refactor (P1-P7, G1-G4) is an ordinary-maintenance addition on top of that completed baseline, not a phase re-open.

## Infrastructure as Code

`infra/` contains the Bicep deployment baseline: top-level `main.bicep`, resource modules, deployment scripts, and rollback contracts. `deploy/compose/` contains the Portable-lane Docker Compose baseline (Caddy, PgBouncer, MinIO, images.env, VPS runbook) - see `deploy/compose/README.md`.

Deployment plan: [`.azure/deployment-plan.md`](../.azure/deployment-plan.md).

Validate locally with `az bicep build --file infra/main.bicep` and `docker compose -f deploy/compose/docker-compose.yml config -q`.

## Outstanding Follow-Ups

1. Aspire mesh (`Test.Aspire` DCP-dependent lanes) and the full-stack `Test.PlaywrightUI` lane are unverified on this machine: Aspire/DCP binds published container ports to `127.0.0.1` inside the Podman WSL VM, unreachable from the Windows host, independent of the Testcontainers `TESTCONTAINERS_HOST_OVERRIDE` workaround used by the other container lanes. Needs either Docker Desktop or a podman machine networking change before these lanes can run here.
2. EF.* package requests from `docs/plans/ef-package-requests.md`: all REQUIRED items landed as of EF.* 1.1.102 / EF.FlowEngine.*/EF.FilterBuilder 1.0.173, except request 3 (partial - EF.Data.SqlServer split shipped, `Microsoft.Data.SqlClient` stays transitive until EF.Data 2.0, open by design) and request 4 (landed, not adopted - D-004, no repository asks for NOLOCK). Request 19 (a UUIDv7-shaped FlowEngine loop iteration id) is rejected for now: `LoopNodeExecutor.IterationId` is a deterministic UUIDv5 and GR-17 rejects a non-UUIDv7 client create id with 400, so the decomposer loop keeps its own `idempotencyKey` instead. `EF.Audit.Data` and `EF.Audit.AzureTable` (published 1.1.101/1.1.102) are rejected: both stamp the consumer clock into `RecordedUtc`/`PartitionKey`/`RowKey`, reproducing the A1 replay-duplicate defect, and `EF.Audit.Data` additionally mandates its own `AuditDbContext`; reconciliation stays contracts-only via `EF.Audit.Contracts`, with the id-derived key scheme (D-058) implemented app-side. See the dated "Feedback after adoption (2026-09-10)" section in `ef-package-requests.md` for the remaining post-merge findings (KeysetProjection selector shape, envelope tenant slot, sender-pool enumeration, envelope serializer JsonTypeInfo support, trace-context Baggage, EF.Messaging's Azure SDK footprint).
3. The 5,000 RPS load gate is deployment-only by design (`Test.Load` is manual/NBomber); local proof is Testcontainers plus the million-row fixture, not a live RPS measurement.
4. Authenticated Azure AI persistence and enqueue scenarios remain deployment-only.
5. Browser WASM trimming remains disabled for Release because upstream Uno dependencies emit `IL2104` under warnings-as-errors; unaffected by this refactor, not re-verified this pass.
6. `TaskItemRescheduledEvent` remains defined and versioned in the envelope map but never constructed anywhere in the merged code - still dead code, still not removed.
7. Watch items recorded by `docs/plans/scale-guidance-alignment.md` and the G3/P6 session log. Resolved by slice F1 (2026-09-09): floating image tags in `deploy/compose/` now pinned (`grafana/otel-lgtm:0.32.1`, `minio/minio:RELEASE.2025-09-07T16-13-09Z`, `edoburu/pgbouncer:v1.25.2-p0`, including the Aspire AppHost MinIO container); `launchSettings.json`'s `applicationUrl` no longer conflicts with `Kestrel:Endpoints` on a local `dotnet run` (the Api's `applicationUrl` and `https` profile were removed, `Kestrel:Endpoints` is the single port source); `TaskFlow.Functions.csproj` no longer marks `OpenAI`/`Microsoft.Extensions.AI.OpenAI` `PrivateAssets="all"` (both are deployed dependencies, matching Api/Bootstrapper; `Microsoft.AI.Foundry.Local` keeps it, unrelated); App Configuration is now wired into the Functions host via `Microsoft.Azure.AppConfiguration.Functions.Worker`. Still open: `Pgvector.EntityFrameworkCore` 0.3.0 is the newest published release (verified 2026-09-09 against nuget.org), targets `net8.0`, declares `Npgsql.EntityFrameworkCore.PostgreSQL >= 9.0.1`, and runs on EF Core 10/Npgsql 10.0.3 by framework roll-forward - `PgVectorSearchTests` (PostgreSql lane) is the running proof; upgrade path is to bump the pin when a net10/EF Core 10 build ships, or vendor the type-mapping plugin in-repo if the roll-forward ever breaks. Also still open: a second consecutive solution build emits ~180 `ExtensionsMetadataGenerator` warnings from a stale `TaskFlow.Functions` `obj` tree (clean-`obj` build is 0 warnings).
