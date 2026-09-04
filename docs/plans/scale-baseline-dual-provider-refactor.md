# Scale Baseline Dual-Provider Refactor - Orchestration Handoff

Durable state for the orchestrated refactor. Sessions and agents are disposable; this doc plus `docs/plans/scale-baseline-dual-provider-plan.md` (the approved plan) must be enough to resume.

## Goal

Implement the approved plan: dual EF Core provider (SQL Server + PostgreSQL) with one common code path, provider-neutral concurrency token + ETag/If-Match across all layers and clients, caller-supplied UUIDv7 idempotent create, cursor paging, transactional outbox + consumer inbox + work tables, real scheduler jobs with retention, FusionCache activation, Redis-backed rate limiting, streaming/summary/metadata endpoints, Bicep scale-out profiles, packages at latest. No history or backward compatibility to keep; migrations reset as a fresh baseline.

## Decisions (confirmed by the user 2026-09-04)

- Composite tenant-first primary keys `(TenantId, Id)` on every tenant entity (D-022).
- One app-layer column encryption (AES-GCM + HMAC blind index) on both providers; Always Encrypted documented only (D-023 supersedes D-019).
- Generated shared API clients: Refitter (Blazor + Uno via new `src/UI/TaskFlow.ApiClient`), openapi-typescript for React.
- Delivery: integration branch `feature/scale-baseline-dual-provider` from `main`; slice branches merge into it locally; one integration commit per green phase; no push, no PR until the user asks.
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
| S2 | Phase 1.1-1.4 + 1.6 data layer: provider switch, model (composite PK, Version, UTC, encryption, inbox/outbox/work entities + configs), 6 migration sets, migrator, validator, AppHost, test fixtures both providers | fable | blocked on S0 | feature/sb-s2-data-layer | ../scaffold-proof-wt/s2 | - |
| S3 | Phase 1.5 + Phase 4 + 3.2 Bicep: postgres module, provider param, Hyperscale prod profile, Redis, Container Apps scale rules, Service Bus dup detection + 3 subscriptions, Functions scale limit, dev/prod bicepparam, infra docs reconciled | sonnet | running | feature/sb-s3-infra | ../scaffold-proof-wt/s3 | a07e88a63d209ca71 |
| S4 | Phase 2.1-2.5 contract: DTO Version, concurrency guard, cursor paging, idempotent create, read service + endpoints, filters, exception mapping, OpenAPI transformer, both styles, repository keyset/summary/export, Test.Endpoints dual-style harness, Test.Architecture rules | opus | blocked on S2 | feature/sb-s4-contract | ../scaffold-proof-wt/s4 | - |
| S5 | Phase 2.6 clients: Refitter shared client, Blazor, React (openapi-typescript), Uno, FlowEngine workflow JSON, AI tools, Functions, Test.UI, Playwright | sonnet | blocked on S4 | feature/sb-s5-clients | ../scaffold-proof-wt/s5 | - |
| S6 | Phase 3.1-3.2 messaging: outbox staging interceptor + domain events, claim/dispatch workers, transport, envelope, Functions split + DLQ, consumer inbox guard, projection patch/continuation, Aspire SB subscriptions | opus | blocked on S2 | feature/sb-s6-messaging | ../scaffold-proof-wt/s6 | - |
| S7 | Phase 3.3-3.6: system repository + real scheduler jobs, retention, FusionCache abstraction + call sites, Redis rate limiter, token single-flight, provisioning startup task, search routing, clamps, telemetry, health | opus | blocked on S4 and S6 | feature/sb-s7-scheduler-cache | ../scaffold-proof-wt/s7 | - |
| S8 | Docs: tech-design.html patches, README, infra docs, REFERENCE-STATUS from observed results, scaffold-instructions-handoff.md, refresh ef-package-requests.md | sonnet | blocked on all | feature/sb-s8-docs | ../scaffold-proof-wt/s8 | - |

Wave plan: wave 1 = S0, S1, S3 (parallel). Wave 2 = S2 (after S0 merged). Wave 3 = S4, S6 (after S2 merged). Wave 4 = S5, S7 (after S4 and S6 merged). Wave 5 = S8 after full acceptance.

## Merge gate per slice

Diff reviewed by the orchestrator in the agent's worktree; `dotnet build TaskFlow.slnx` and the Uno build at 0 warnings; `dotnet test TaskFlow.slnx --filter "TestCategory=Unit|TestCategory=Architecture|TestCategory=Endpoint"` green; slice-specific Docker lanes where the slice touched them; `az bicep build` for infra. Then `git merge --no-ff feature/sb-<slice>` into the integration branch in the primary checkout.

## Session log

- 2026-09-04: plan approved; integration branch created from main 86908b6; handoff, plan copy, package requests committed. Next action: spawn wave 1 (S0 with harness worktree, S1 and S3 with in-prompt worktrees).
- 2026-09-04: S1 merged (27897bc). Retention job targetService/method names in resource-implementation.yaml are inferred placeholders; S7 reconciles them against real interfaces.
- 2026-09-04: S0 merged. EF.* 1.1.98 is a republish (public API identical to 1.0.95), so every package request in ef-package-requests.md still stands. OpenAI stays 2.12.0 (Microsoft.Extensions.AI.OpenAI 10.9.0 pins [2.12.0,2.13.0)). ASPIRE010 suppressed in AppHost.csproj with removal criteria. dotnet-ef 10.0.11 added to dotnet-tools.json. Next: spawn S2 data layer from the integration branch.
