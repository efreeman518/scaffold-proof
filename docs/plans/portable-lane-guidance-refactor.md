# Portable Lane + Guidance Alignment Refactor - Orchestration Handoff

Durable state for the orchestrated refactor. Sessions and agents are disposable; this doc plus `docs/plans/portable-lane-guidance-plan.md` (the approved plan) must be enough to resume.

## Goal

Add the Portable hosting lane (Docker Compose + Caddy on a VPS, external PostgreSQL and LLM provider, Azure only for Key Vault + App Configuration) as independent provider switches with a lane preset, and close every gap against the scale-guidance checklist (runtime profile, source-generated JSON, hot paths, bounded concurrency, health probe contract, edge hardening, hedging, distributed lock, broker trace propagation, internal gRPC, MessagePack L2, feature flags, pgvector search). Plan sections: Part A (lane), Part B (alignment), decisions D-035..D-055.

## Decisions (confirmed by the user 2026-09-08)

- Keep the own transport (`IIntegrationEventTransport`, `EF.Messaging.RabbitMq`); no MassTransit or SlimMessageBus (D-046).
- Portable compute = Docker Compose + Caddy on one VPS, hand-written under `deploy/compose/`; no `Aspire.Hosting.Docker` (D-036).
- Build now: pgvector semantic search (P7), gRPC internal read proof (G3), MessagePack L2 serializer (G3).
- Delivery: slices merge into `feature/scale-baseline-dual-provider` in the primary checkout; one integration commit per green slice; no push, no PR until the user asks.
- Health paths fixed for every slice: `/healthz/live`, `/healthz/ready`, `/healthz` aggregate (D-049). gRPC h2c port for the Api: 8081 (D-054).
- Environment: Testcontainers lanes need a run-scoped `TESTCONTAINERS_HOST_OVERRIDE=<podman machine ip>` (never committed); Test.Aspire and compose smoke cannot run on this machine (Podman WSL port binding) - CI is the proof.

## Slice table

Worktrees live in `C:\Users\EbenFreeman\source\repos\scaffold-proof-wt\<slice>` unless the harness chose its own path. Base for every slice branch: `feature/scale-baseline-dual-provider` at spawn time.

| id | scope (plan sections) | model | status | branch | worktree | agent id |
|---|---|---|---|---|---|---|
| P1 | Artifacts: D-035..D-055, resource-implementation.yaml, UBIQUITOUS-LANGUAGE, domain-specification, INSTRUCTION-GAPS | sonnet | done (merged) | feature/pl-p1-artifacts | removed | a2c5c8672f43fea50 |
| G1 | Runtime props + Dockerfiles, TaskFlowJsonContext, export writer, pooled publish buffers, bounded blob deletes + dispatcher fan-out, blocking-call sweep, package AOT flags, benchmark (B.2 G1) | opus | done (merged) | feature/pl-g1-runtime | removed | a57532ee14de929e6 |
| P2 | Six switch skeletons with existing arms + fallbacks, Data Protection switch, lifted credential, pooler option, selector tests, architecture rules (A.1) | sonnet | done (merged) | feature/pl-p2-switches | removed | a8d6f9235007166e8 |
| G2 | Health contract + Bicep probes/sticky, YARP hardening + edge limiter, hedging, ActivitySources + trace propagation, IDistributedLock (B.2 G2) | opus | running | feature/pl-g2-edge-health-tracing | scaffold-proof-wt/pl-g2 | a5d39f4fa0c943b96 |
| P3 | Relational read model + relational audit: entities, configs, migrations both providers, repositories, arms, parity tests (A.1) | opus | pending | feature/pl-p3-relational-readmodel-audit | - | - |
| P4 | S3 object storage + MinIO fixture + health + provisioning + endpoint tests (A.1) | sonnet | pending | feature/pl-p4-s3 | - | - |
| P5 | App Configuration + FeatureManagement + tenant targeting + flags + OpenAI-compatible arm (A.1) | sonnet | pending | feature/pl-p5-appconfig-flags-ai | - | - |
| G3 | gRPC read service + Blazor client + ports; MessagePack L2 switch (B.2 G3) | opus | pending | feature/pl-g3-grpc-messagepack | - | - |
| P6 | Compose lane, Aspire lane preset + MinIO, reusable image workflow + deploy-vps.yml, ci compose config/smoke, Bicep pgbouncer, Test.Aspire portable topology (A.2) | opus | pending | feature/pl-p6-compose-lane | - | - |
| P7 | PgVector semantic search: entity + migration, embedding consumer + topology, search service, fail-fast, tests (A.1) | opus | pending | feature/pl-p7-pgvector | - | - |
| G4 | LoggerMessage sweep + CA1848, alignment doc, tech-design, README, REFERENCE-STATUS, scaffold-instructions-handoff, ef-package-requests, language (B.2 G4) | sonnet | pending | feature/pl-g4-docs | - | - |

Waves: 1 = P1 + G1; 2 = P2 + G2 (after wave 1 merged); 3 = P3 + P4 + P5 + G3 (after wave 2 merged); 4 = P6 + P7 (after wave 3 merged); 5 = G4 after full acceptance.

## Merge gate per slice

Diff reviewed by the orchestrator in the agent's worktree; `dotnet build TaskFlow.slnx` and the Uno build at 0 warnings; `dotnet test TaskFlow.slnx --filter "TestCategory=Unit|TestCategory=Architecture|TestCategory=Endpoint"` green; Testcontainers lanes the slice touched on both `TASKFLOW_TEST_DB_PROVIDER` values; `az bicep build` when infra changed; `docker compose config -q` when compose changed; vulnerability audit clean. Then `git merge --no-ff feature/pl-<slice>` into the integration branch in the primary checkout.

## Session log

- 2026-09-08: inventories (Azure surfaces, runtime alignment, infra assets) and the portable-lane design completed; the guidance-alignment design agent was stopped by a user interrupt, so the alignment slicing was done by the orchestrator from the inventory evidence. User decisions recorded above. Plan and handoff committed. Next action: spawn wave 1 (P1 with harness worktree, G1 with in-prompt worktree).
- 2026-09-08: P1 merged (artifacts). Note: new entities TaskView/AuditLog/TaskItemEmbedding sit in the entities list tagged with dataStore readModel/audit/postgresOnly; the embedding path is a fourth subscription on DomainEvents bound to TaskItemCreatedEvent, TaskItemStatusChangedEvent, TaskItemRescheduledEvent, TaskItemCompletedEvent (no generic TaskItemUpdatedEvent exists). aiServices.search.provider is now a list spelled AzureAiSearch. G1 running.
- 2026-09-08: G1 merged (7 commits, 38 files). Fast lane now 498 (Unit|Architecture|Endpoint). Shapes downstream slices must use: TaskFlowJsonContext (Application.Models), TaskFlowMessagingJsonContext (Application.Contracts, envelope + event payloads; IntegrationEventEnvelope.From THROWS for an unregistered event record - register new events there), TaskFlowApiJsonContext (Api, ProblemDetails); src/Host/TaskFlow.Host.props imported by the six hosts; Blazor and Functions Dockerfiles use aspnet:10.0-noble-chiseled-extra (ICU) with an architecture test pinning the pairing; EnableRequestDelegateGenerator on in the Api; EF.Messaging.RabbitMq IsAotCompatible; Api/Gateway Dockerfiles publish ReadyToRun (-r linux-x64 on restore too). OPEN ITEM for P5: the Api publish output contains Microsoft.Agents.AI.OpenAI.dll but NOT OpenAI.dll (OpenAI is PackageReference PrivateAssets=all in Api and Bootstrapper, hence PublishReadyToRunExclude in TaskFlow.Api.csproj). The OpenAICompatible arm needs OpenAI.dll deployed: P5 must make OpenAI a deployed dependency (resolve whatever version pin motivated PrivateAssets=all) and remove the R2R exclude. Benchmark: export 1,000 rows 192,072 B -> 136 B allocated. P2 running; G2 spawned.
- 2026-09-08: P2 merged (33 files). Fast lane 561. Shapes: HostingLane/HostingLaneSelector in Application.Contracts/Configuration (env TASKFLOW_LANE > Hosting:Lane > Azure); Bootstrapper Registration/HostingLane.cs holds LaneDefaults.Portable; selectors RegisterServices.{Storage,ReadModel,Audit,DataProtection}.cs + Search in Infrastructure.AI/ServiceCollectionExtensions.cs + AI in RegisterServices.AiChatClient.cs, each env > config > lane default > hard default, unknown value throws; unimplemented arms throw NotSupportedException naming the slice (S3 -> P4, Relational -> P3, PgVector -> P7, OpenAICompatible -> P5); [ProviderSwitch(typeof(contract))] attribute on dispatchers drives ProviderSwitchArchitectureTests; NoOpBlobStorageRepository registered when AzureBlob has no connection string; Data Protection lifted to RegisterServices.DataProtection.cs (Redis arm eager ConnectionMultiplexer.Connect, documented shortcut); PoolerMode in TaskFlowDbProvider.cs; AGENTS.md documents the new env vars. System.Security.Cryptography.Xml bumped to 10.0.12 (NU1605). G2 running; spawning P3, P4, P5.
