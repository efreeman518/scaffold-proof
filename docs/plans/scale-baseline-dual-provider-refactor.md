# Scale Baseline Dual-Provider Refactor - Orchestration Handoff

Durable state for the orchestrated refactor. Sessions and agents are disposable; this doc plus `docs/plans/scale-baseline-dual-provider-plan.md` (the approved plan) must be enough to resume.

## Goal

Implement the approved plan: dual EF Core provider (SQL Server + PostgreSQL) with one common code path, provider-neutral concurrency token + ETag/If-Match across all layers and clients, caller-supplied UUIDv7 idempotent create, cursor paging, transactional outbox + consumer inbox + work tables, real scheduler jobs with retention, FusionCache activation, Redis-backed rate limiting, streaming/summary/metadata endpoints, Bicep scale-out profiles, packages at latest. No history or backward compatibility to keep; migrations reset as a fresh baseline.

## Decisions (confirmed by the user 2026-09-04)

- Composite tenant-first primary keys `(TenantId, Id)` on every tenant entity (D-022).
- One app-layer column encryption (AES-GCM + HMAC blind index) on both providers; Always Encrypted documented only (D-023 supersedes D-019).
- Generated shared API clients: Refitter (Blazor + Uno via new `src/UI/TaskFlow.ApiClient`), openapi-typescript for React.
- Delivery: integration branch `feature/scale-baseline-dual-provider` from `main`; slice branches merge into it locally; one integration commit per green phase; no push, no PR until the user asks.
- D-034 (added 2026-09-04): messaging provider switch ServiceBus | RabbitMq; own thin RabbitMQ.Client wrapper (connection multiplexer, publisher confirms, per-queue prefetch, DLX) instead of SlimMessageBus; outbox stays TaskFlow-owned; RabbitMQ consumers hosted in the Scheduler, Functions Service Bus triggers disabled via AzureWebJobs.<fn>.Disabled when RabbitMq is selected. Plan section 3.7.
- Common-approach rule (D-030): provider branches only where EF Core forces them; everything else one path with a one-line comment naming the provider-specific customization.
- 5,000 RPS load gate is deployment-only; local proof is Testcontainers + million-row fixture.
- Package changes: EF.* packages are ours; requests live in `docs/plans/ef-package-requests.md`. Slices use app-local fallbacks where the plan names one, marked `// fallback: replace with EF.<pkg>.<member> when published`.

## Extra deliverables

- `docs/plans/ef-package-requests.md` - hand-off to the EF.* package coding agent (exists; refresh after Phase 0 confirms which items already shipped in 1.1.98 / 1.0.163).
- `docs/plans/scaffold-instructions-handoff.md` - description of the implemented patterns and their file shapes for the scaffold-ai instructions agent (produced by S8 from the merged result, not from the plan).

## Slice table

Worktrees live in `C:\Users\EbenFreeman\source\repos\scaffold-proof-wt\<slice>` unless the harness chose its own path. Base for every slice branch: `feature/scale-baseline-dual-provider` at spawn time.

| id | scope (plan sections) | model | status | branch | worktree | agent id |
|---|---|---|---|---|---|---|
| S0 | Phase 0: all packages to latest, add Npgsql/Testcontainers.PostgreSql/Aspire.Hosting.PostgreSQL/FusionCache.OpenTelemetry, fix compile breaks, gates | sonnet | done (merged) | feature/sb-s0-packages | removed | a6e49b17ab33322b8 |
| S1 | Artifacts: DESIGN-DECISIONS D-020..D-030 + supersede D-019, domain-specification, UBIQUITOUS-LANGUAGE, resource-implementation, INSTRUCTION-GAPS | sonnet | done (merged 27897bc) | feature/sb-s1-artifacts | removed | a1a7f7c9204ac78a9 |
| S2 | Phase 1.1-1.4 + 1.6 data layer: provider switch, model (composite PK, Version, UTC, encryption, inbox/outbox/work entities + configs), 6 migration sets, migrator, validator, AppHost, test fixtures both providers | fable | done (merged) | feature/sb-s2-data-layer | removed | a90c2662277d7a838 |
| S3 | Phase 1.5 + Phase 4 + 3.2 Bicep: postgres module, provider param, Hyperscale prod profile, Redis, Container Apps scale rules, Service Bus dup detection + 3 subscriptions, Functions scale limit, dev/prod bicepparam, infra docs reconciled | sonnet | done (merged) | feature/sb-s3-infra | removed | a07e88a63d209ca71 |
| S4 | Phase 2.1-2.5 contract: DTO Version, concurrency guard, cursor paging, idempotent create, read service + endpoints, filters, exception mapping, OpenAPI transformer, both styles, repository keyset/summary/export, Test.Endpoints dual-style harness, Test.Architecture rules | opus | blocked on S2 | feature/sb-s4-contract | ../scaffold-proof-wt/s4 | - |
| S5 | Phase 2.6 clients: Refitter shared client, Blazor, React (openapi-typescript), Uno, FlowEngine workflow JSON, AI tools, Functions, Test.UI, Playwright | sonnet | blocked on S4 | feature/sb-s5-clients | ../scaffold-proof-wt/s5 | - |
| S6 | Phase 3.1-3.2 + 3.7 messaging: outbox staging interceptor + domain events, claim/dispatch workers, transport port with ServiceBus AND RabbitMq implementations (Messaging:Provider switch; RabbitMq via ProjectReference to src/Packages/EF.Messaging.RabbitMq built by S9: TaskFlow adapter = RabbitMqEventTransport, topology constants, three IRabbitMqMessageHandler wrappers registered in the Scheduler), envelope, Functions split + DLQ + disabled-when-RabbitMq, consumer inbox guard, projection patch/continuation, Aspire SB subscriptions or RabbitMQ resource, rabbitmq-container-app.bicep, artifacts for D-034 | opus | blocked on S2 and S9 | feature/sb-s6-messaging | ../scaffold-proof-wt/s6 | - |
| S7 | Phase 3.3-3.6: system repository + real scheduler jobs, retention, FusionCache abstraction + call sites, Redis rate limiter, token single-flight, provisioning startup task, search routing, clamps, telemetry, health | opus | blocked on S4 and S6 | feature/sb-s7-scheduler-cache | ../scaffold-proof-wt/s7 | - |
| S9 | Build `src/Packages/EF.Messaging.RabbitMq` (portable package project, namespace EF.Messaging.RabbitMq, exactly per docs/plans/ef-messaging-rabbitmq-package-spec.md) + `tests/EF.Messaging.RabbitMq.Tests` (MSTest, Testcontainers.RabbitMq acceptance tests from the spec); no TaskFlow dependencies; `dotnet pack` ready | opus | done (merged) | feature/sb-s9-rabbitmq-package | removed | ac9350465702ad548 |
| S8 | Docs: tech-design.html patches, README, infra docs, REFERENCE-STATUS from observed results, scaffold-instructions-handoff.md, refresh ef-package-requests.md | sonnet | blocked on all | feature/sb-s8-docs | ../scaffold-proof-wt/s8 | - |

Wave plan: wave 1 = S0, S1, S3 (parallel). Wave 2 = S2 (after S0 merged). Wave 3 = S4, S6 (after S2 merged). Wave 4 = S5, S7 (after S4 and S6 merged). Wave 5 = S8 after full acceptance.

## Merge gate per slice

Diff reviewed by the orchestrator in the agent's worktree; `dotnet build TaskFlow.slnx` and the Uno build at 0 warnings; `dotnet test TaskFlow.slnx --filter "TestCategory=Unit|TestCategory=Architecture|TestCategory=Endpoint"` green; slice-specific Docker lanes where the slice touched them; `az bicep build` for infra. Then `git merge --no-ff feature/sb-<slice>` into the integration branch in the primary checkout.

## Session log

- 2026-09-04: plan approved; integration branch created from main 86908b6; handoff, plan copy, package requests committed. Next action: spawn wave 1 (S0 with harness worktree, S1 and S3 with in-prompt worktrees).
- 2026-09-04: S1 merged (27897bc). Retention job targetService/method names in resource-implementation.yaml are inferred placeholders; S7 reconciles them against real interfaces.
- 2026-09-04: S0 merged. EF.* 1.1.98 is a republish (public API identical to 1.0.95), so every package request in ef-package-requests.md still stands. OpenAI stays 2.12.0 (Microsoft.Extensions.AI.OpenAI 10.9.0 pins [2.12.0,2.13.0)). ASPIRE010 suppressed in AppHost.csproj with removal criteria. dotnet-ef 10.0.11 added to dotnet-tools.json. Next: spawn S2 data layer from the integration branch.
- 2026-09-04: S3 merged. Bicep: postgres-flexible-server (API 2025-08-01), redis + redis-rbac (redisEnterprise 2025-07-01, access-key connection string now, Entra access policies provisioned for a later code-side switch), provider param with conditional modules, Hyperscale prod profile, per-host scale profiles, 3 Service Bus subscriptions with SQL rules + duplicate detection, Query connection string wired, dev/prod bicepparam, 9 new Bicep contract tests. Follow-up for a later slice: Npgsql Entra token auth and StackExchange.Redis Entra auth are not wired in app code.
- 2026-09-04: user added the RabbitMQ transport alternative (plan 3.7, D-034); folded into S6 scope. S2 still running.
- 2026-09-04: user asked to build EF.Messaging.RabbitMq in this repo now, portable to the EF.* package repo later. New slice S9 (runs in parallel with S2): `src/Packages/EF.Messaging.RabbitMq` + `tests/EF.Messaging.RabbitMq.Tests`, spec = docs/plans/ef-messaging-rabbitmq-package-spec.md. S6 consumes it via ProjectReference; porting = move the two projects, publish, swap to PackageReference.
- 2026-09-04: S9 merged: src/Packages/EF.Messaging.RabbitMq (public API exactly per spec; retry bound uses in-process attempts because x-death only records actual dead-lettering) + 31 tests. ENVIRONMENT: container ports are not reachable at localhost (Podman WSL2 forwarding); every Testcontainers lane needs a run-scoped `TESTCONTAINERS_HOST_OVERRIDE=<podman machine ip>` (was 192.168.186.166; changes on reboot; never commit). AGENTS.md "Podman-backed Docker context verified" is stale until fixed. REFERENCE-STATUS.md aggregate counts are stale (44 -> 46 projects); S8 refreshes once from a full run.
- 2026-09-04: S2 merged (10 commits, 113 files). Shapes downstream slices must use: `TaskFlowEntityBase<TId>` (IVersionedEntity: Version/CreatedAtUtc/ModifiedAtUtc, private setters written by `VersionTimestampInterceptor`), `TaskFlowDbProviderSelector`/`UseTaskFlowProvider`, `Infrastructure.Data/Encryption/*` (IColumnEncryptor, UseColumnEncryption), `Operational/OperationalWorkBase.cs` (OutboxMessage, BlobDeleteWork, ConsumerInbox) + `IInboxStore.TryClaimAsync` (FlexLabs upsert), migrations in `TaskFlow.Infrastructure.Data.Migrations.{SqlServer,PostgreSql}` (design-time factories in DatabaseMigrator), `Test.Support/Hosting/TestDatabaseContainer.cs` + `Test.Integration/Infrastructure/DbContainerFixture.cs` + `Test.E2E/DbApiFactory.cs` (env TASKFLOW_TEST_DB_PROVIDER), open-generic repositories re-implement GetAsync for the composite key, `ICategoryRepositoryTrxn.ClearCategoryFromTaskItemsAsync`. Risks: SQL Server RecurrencePattern uses the json type (SQL Server 2025+); timestamptz is microsecond precision. Integration gate (build + fast lanes) running on the merged branch before wave 3.
