# REFERENCE-STATUS - TaskFlow

Canonical current evidence for the TaskFlow reference application. Historical phase narrative belongs in Git history; root `HANDOFF.md` contains only terminal routing state.

> Update this file only from observed results. TaskFlow CI records the scaffold checkout commit used for cross-repository validation so failures remain diagnosable without creating a compatibility pin.

## Build Status

| Field | Value |
|---|---|
| Last verified | 2026-09-04 |
| Solution | `TaskFlow.slnx` |
| Target framework | .NET 10 |
| Solution projects | 48 |
| Errors | 0 |
| Warnings | 0 |

`src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj` builds separately (3 projects: `TaskFlow.Uno`, `TaskFlow.Uno.Core`, `TaskFlow.Uno.Presentation`) because the Uno SDK requires explicit invocation; 0 errors, 0 warnings.

Note on the project count: `TaskFlow.slnx` declares 48 distinct project entries (verified by grep and by `dotnet sln TaskFlow.slnx list`), and `dotnet list ... package --vulnerable` enumerated the same 48 project names individually. `dotnet build TaskFlow.slnx`'s own summary line reports "51 projects" built. The 3-project delta is observed but not root-caused on this pass (not guessed at) - flagged here rather than reconciled.

New projects added by this refactor: `src/Packages/EF.Messaging.RabbitMq` (portable RabbitMQ transport, no TaskFlow dependencies), `src/Infrastructure/TaskFlow.Infrastructure.Data.Migrations.SqlServer` and `...Migrations.PostgreSql` (D-025 per-provider migration assemblies), `src/Infrastructure/TaskFlow.Infrastructure.Caching` (FusionCache + Redis rate limiting), `src/Infrastructure/TaskFlow.Infrastructure.Messaging.RabbitMq` (RabbitMQ adapter, D-034), `src/UI/TaskFlow.ApiClient` (Refitter-generated shared client), `tests/EF.Messaging.RabbitMq.Tests`.

## Test Status

Dual-provider lanes ran once per `TASKFLOW_TEST_DB_PROVIDER` value (SqlServer, PostgreSql) on the merged branch at commit `d6067ae` (2026-09-04). Container-free categories (Unit, Architecture, Endpoint, and the RabbitMQ package's own unit lane) were rerun directly in this worktree after a clean build and matched the values below exactly.

| Project | Category filter | Verified count | Notes |
|---|---|---:|---|
| Test.Unit | `TestCategory=Unit` | 308 | Rerun in-worktree. Includes scaffold auth, deployment and Bicep contracts, dual-provider selector, concurrency token, cursor paging, application-style switching, AI contracts, middleware, caching, FlowEngine, functions, scheduler, repositories, mappers, and services |
| Test.Architecture | `TestCategory=Architecture` | 26 | Rerun in-worktree. Layering, naming, generated-code, endpoint-route, style-parity, and project coverage checks |
| Test.Endpoints | `TestCategory=Endpoint` | 143 | Rerun in-worktree. In-memory WebApplicationFactory coverage for both application styles: 412/428/ETag, replay/conflict, cursor paging, summary/metadata/export |
| EF.Messaging.RabbitMq.Tests | `TestCategory=Unit` | 15 | Rerun in-worktree. Topology declaration idempotence, connection multiplexer channel pool bounds against a fake channel |
| EF.Messaging.RabbitMq.Tests | `TestCategory=Integration` | 16 | Testcontainers.RabbitMq lane; provider-independent of the DB provider selection |
| Test.UI | `UI`, `Presentation` | 56 | Headless UI and presentation contracts, including cursor/ETag client coverage and source-generated JSON |
| Test.Mutation | n/a | 33 | Mutation-target contract tests |
| Test.Integration | `TestCategory=Integration` | 45 (SqlServer) / 45 (PostgreSql) | Real DB + Azurite component coverage; outbox claim concurrency, retention sweeps, FusionCache tag invalidation over Redis, export streaming |
| Test.E2E | `TestCategory=E2E` | 10 (SqlServer) / 10 (PostgreSql) | Real-DB multi-endpoint workflows for both application styles; cursor semantics, concurrent-update 412 race |
| Test.Integration.FlowEngine | `TestCategory=Integration` | 18 (SqlServer) / 18 (PostgreSql) | Workflow JSON validity and engine integration contracts, including the If-Match:* connector override |
| Test.Aspire | `Aspire`, `Foundry`, `Integration`, `LiveAI` | not runnable here | Aspire/DCP binds published container ports to `127.0.0.1` inside the Podman WSL VM, unreachable from Windows; independent of the Testcontainers host override |
| Test.PlaywrightUI (full-stack) | `PlaywrightUI` | not runnable here | Same Aspire AppHost startup dependency as Test.Aspire |
| Test.FoundryLocal | `FoundryLocal`, `LiveAI` | not runnable here | Live local-model lane; requires the RID-bound Foundry Local runtime |
| Test.Mobile | `MobileUI` | not runnable here | Requires a dedicated Android emulator/Appium runner |
| Test.Load | `TestCategory=Load` | not run | Manual; the 5,000 RPS gate is deployment-only |
| Test.Benchmarks | n/a | - | BenchmarkDotNet console runner; build-verified only |

Combined fast gate `TestCategory=Unit|TestCategory=Architecture|TestCategory=Endpoint` across the whole solution: 476 passed, 0 failed (one test in `Test.Unit` also carries `TestCategory=Endpoint`, so the per-project sum of 477 collapses to 476 distinct executions under the combined filter). Solution build: 51 projects (see note above), 0 errors, 0 warnings. Uno build: 3 projects, 0 errors, 0 warnings. Deployment Dockerfiles use non-root chiseled runtime stages; SDK images remain build-stage only.

Not runnable on this machine (recorded with reason, not treated as failing): Test.Aspire and the Test.PlaywrightUI full-stack lane (Aspire/DCP port-binding, above), Test.Mobile (dedicated Appium runner), Test.FoundryLocal (live model lane), Test.Load (manual; deployment-only load gate), Test.Benchmarks (build-verified only, not a pass/fail count).

Published Release Uno cold-start and normal browser projects pass from empty browser state without refresh, retry, sleep, or exception suppression. Browser WASM Release temporarily sets `PublishTrimmed=false` because the current Navigation, Toolkit, and WinUI package set emits upstream `IL2104` under warnings-as-errors. Removal condition: those packages become trim-clean. Validation gate: rerun clean Release publish plus the Uno cold-start and normal Playwright projects before removing the workaround. Re-verified 2026-09-04 after the Phase 0 package bump (Uno.Extensions.Navigation/.WinUI 7.3.6, Uno.Sdk 6.7.22): a forced `PublishTrimmed=true` Release publish still fails with `IL2104` from `Uno.Extensions.Navigation`, `Uno.Extensions.Navigation.UI`, and `Uno.Toolkit.WinUI` 9.1.3; the workaround remains necessary.

## Vulnerability Status

Run `dotnet list package --vulnerable --include-transitive` and capture findings here. Severity policy: [scaffold execution gates](https://github.com/efreeman518/scaffold-ai/blob/main/support/execution-gates.md#vulnerability-audit).

Last audit (2026-09-04, this worktree) ran `dotnet list TaskFlow.slnx package --vulnerable --include-transitive` (sources: `api.nuget.org`, `nuget.pkg.github.com/efreeman518`) and reported, for every one of the 48 projects individually, "has no vulnerable packages given the current sources" - no vulnerable packages or advisories, direct or transitive.

| Package | Severity | Direct/Transitive | Advisory | Notes |
|---|---|---|---|---|
| _None_ | - | - | - | Full solution audit reported no vulnerable packages |

## Capability Coverage

Status meanings:

- `proven`: implemented and covered by executable build, test, or smoke evidence.
- `deployment-only`: generated or wired, but live acceptance requires deployed external resources or identity.
- `documented-only`: example or opt-in documentation exists without active runtime wiring.
- `not enabled`: intentionally absent from the TaskFlow configuration.

| Capability | Status | Evidence boundary |
|---|---|---|
| Service and CQRS application-style switch | proven | Shared Endpoint and E2E suites run both styles on both DB providers; `ApplicationStyleResolver` owns selection |
| Dual EF Core provider (SQL Server + PostgreSQL) | proven | `TaskFlowDbProviderSelector`/`UseTaskFlowProvider`; Unit, Integration, E2E, and migrator contracts run once per `TASKFLOW_TEST_DB_PROVIDER` value with matching pass counts on both |
| Composite tenant-first PK, app-managed Version/ETag, migrations | proven | `EntityBaseConfiguration` composite `(TenantId, Id)` key; `VersionTimestampInterceptor`; two migration assemblies (`...Migrations.SqlServer`, `...Migrations.PostgreSql`) |
| ETag / If-Match / 412 / 428 concurrency | proven | `ConcurrencyGuard`, `IfMatchEndpointFilter`, `ETagEndpointFilter`; Endpoint and E2E cases cover missing/malformed/wildcard/stale If-Match on every mutating route |
| Caller-supplied UUIDv7 idempotent create | proven | `UuidV7`/`IdempotentCreateGuard`; Endpoint cases cover non-v7 400, equivalent replay 200, divergent 409 |
| Cursor paging (task items) | proven | `CursorSearchRequest`/`CursorPage`/`ICursorProtector`; Endpoint/E2E cases cover tamper, cross-tenant, sort-mode mismatch, and no-dup/no-gap page-through |
| App-layer column encryption + blind index | proven | `Infrastructure.Data/Encryption/*` (AES-GCM + HMAC blind index) on both providers; Always Encrypted (D-019) kept documented-only, superseded by D-023 |
| Transactional outbox + consumer inbox | proven | `OutboxStagingInterceptor`, `OperationalWorkRepository` lease claim, `OutboxDispatcherService`; `ConsumerInbox`/`IInboxStore.TryClaimAsync`; Integration cases cover claim concurrency and lease-expiry dead-letter survival |
| Messaging transport switch (Service Bus / RabbitMQ) | proven | `Messaging:Provider`; `RabbitMqEventTransport` + `src/Packages/EF.Messaging.RabbitMq` (own 31 tests, Testcontainers.RabbitMq); Service Bus path proven via Bicep + Functions triggers, RabbitMQ path via package + adapter tests |
| Redis cache (FusionCache) and rate limiter | proven | `TaskFlow.Infrastructure.Caching` (`FusionTaskFlowCache`, profiles/tags) and `FailOpenRateLimiter` over `RedisRateLimiting`; Integration cases cover tag invalidation over the Redis backplane and fail-open behavior |
| Generated API clients (Refitter, openapi-typescript) | proven | `src/UI/TaskFlow.ApiClient` (Refitter, Blazor) and React `types.ts` (openapi-typescript) regenerate from the committed OpenAPI document; drift-guard test asserts the committed document is current |
| Aspire, Gateway, Scheduler, Functions | proven (mesh unverified here) | Build, topology, unit, endpoint coverage; the shared Aspire mesh lane (Test.Aspire) cannot run on this machine - see Test Status |
| Uno, Blazor, React | proven | Build, Test.UI, and dedicated mobile evidence; full-stack Playwright blocked here by the same Aspire limitation |
| FlowEngine | proven | Runtime wiring, separate-schema migration, definition/integration cases including the If-Match:* connector override (D-032), unit coverage |
| Foundry Local inference | proven (not rerun this pass) | Dedicated live lane requires the RID-bound Foundry Local runtime, not run in this session (see Test Status); provider-status contract stays unit-covered |
| GitHub Actions and deployment workflow shape | proven | Workflow contract tests and CI execution |
| Bicep module shape (incl. Postgres, Redis, RabbitMQ container app, scale rules) | proven | `az bicep build` and Bicep contract tests cover the provider-conditional modules and dev/prod parameter files |
| Live Entra or CIAM sign-in | deployment-only | Scaffold auth is the local proof; live app registrations, consent, roles, and redirect URIs require deployment |
| Azure Foundry and Azure AI Search | deployment-only | Provider wiring and gated smoke tests exist; live resources are not required for local acceptance |
| Key Vault backed encryption and data-protection keys | deployment-only | AppHost and Bicep wiring exist; live vault, CMK, identity, and RBAC require deployment |
| 5,000 RPS load gate | deployment-only | Test.Load exists (NBomber) but is manual; local proof is Testcontainers + the million-row fixture, not the RPS figure itself |
| Production infrastructure rollout | deployment-only | Deployment workflow and Bicep are validated without asserting a live environment rollout |
| Existing Foundry account, prompt agent, and pre-existing agent opt-ins | documented-only | Commented examples only; not active runtime branches |
| Notifications | not enabled | `includeNotifications: false`; no notification definitions |
| `azd` orchestration | not enabled | `includeAzd: false` |
| Private endpoints | not enabled | `usePrivateEndpoints: false` |

The declared flags and matrix must agree with `.scaffold/resource-implementation.yaml`. Proof paths are validated against the scaffold-owned [TaskFlow proof map](https://github.com/efreeman518/scaffold-ai/blob/main/support/taskflow-proof-map.md).

## Phase Completion

Phases 1 through 5e and the FlowEngine extension are complete. Root `HANDOFF.md` records `workflowStatus: complete`, `currentPhase: 5`, and `currentSubPhase: complete`; future work uses ordinary maintenance.

## Infrastructure as Code

`infra/` contains the Bicep deployment baseline: top-level `main.bicep`, resource modules, deployment scripts, and rollback contracts.

Deployment plan: [`.azure/deployment-plan.md`](../.azure/deployment-plan.md).

Validate locally with `az bicep build --file infra/main.bicep`.

## Outstanding Follow-Ups

1. Aspire mesh (`Test.Aspire`) and the full-stack `Test.PlaywrightUI` lane are unverified on this machine: Aspire/DCP binds published container ports to `127.0.0.1` inside the Podman WSL VM, unreachable from the Windows host, independent of the Testcontainers `TESTCONTAINERS_HOST_OVERRIDE` workaround used by the other container lanes. Needs either Docker Desktop or a podman machine networking change before these two lanes can run here.
2. EF.* package requests from `docs/plans/ef-package-requests.md` remain open against the EF.* package feed: 9 of the 11 REQUIRED requests (1, 2, 5, 6, 7, 8, 9, 10, 11) have an app-local fallback already in the merged code (marked `// fallback:` at the call site) so the repo is not blocked; requests 3 and 4 (splitting SQL Server-only pieces out of EF.Data, and a provider-neutral `ReadIsolation` replacement for `bool readNoLock`) have no fallback and remain genuinely open. Request 18 (FlowEngine per-node `Headers` on `IntegrationNodeConfig`) is confirmed real against the installed `EF.FlowEngine` 1.0.163 package XML docs - no `Headers` property exists; the app-side mitigation is `FlowEngineIfMatchOverrideHandler` (D-032), removal criterion = the package ships `Headers`. Request 23 (extract `EF.Messaging.RabbitMq` as a published package) is fulfilled in-repo as the portable `src/Packages/EF.Messaging.RabbitMq` project, consumed by `TaskFlow.Infrastructure.Messaging.RabbitMq` via `ProjectReference` pending publication.
3. The 5,000 RPS load gate is deployment-only by design (`Test.Load` is manual/NBomber); local proof is Testcontainers plus the million-row fixture, not a live RPS measurement. Do not treat a local run as satisfying the load gate.
4. Authenticated Azure AI persistence and enqueue scenarios remain deployment-only. Close them with a provisioned Azure Foundry and AI Search environment plus real authenticated side-effect assertions; prompt-only model responses are insufficient.
5. Browser WASM trimming remains disabled for Release because upstream Uno dependencies emit `IL2104` under warnings-as-errors. Remove only after a trim-clean dependency update and a clean published cold-start plus normal Uno Playwright run.
6. `TaskItemRescheduledEvent` is defined and versioned in the envelope map but never constructed anywhere in the merged code - a dead event type observed while refreshing this file, not yet removed.
