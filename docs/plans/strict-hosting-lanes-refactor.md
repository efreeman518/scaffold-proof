# Strict Hosting Lanes Refactor

## Goal

Deliver two coherent hosting and container profiles, `Azure` and `NonAzure`, across runtime configuration, Aspire, Testcontainers, deployment, and all three web UIs. PostgreSQL JSONB is the default NonAzure read model; MongoDB is an explicit NonAzure alternative.

## Decisions

- `TASKFLOW_LANE=Azure|NonAzure` is canonical. `Portable` remains a deprecated input alias for one release.
- Lane-owned core providers reject conflicts. Azure uses SQL Server, Service Bus, Azure Blob, Cosmos, Azure Table, and Blob Data Protection. NonAzure uses PostgreSQL, RabbitMQ, S3, relational audit, and Redis Data Protection.
- NonAzure read model permits `PostgreSqlJsonb` by default or `MongoDb` explicitly. Legacy `Relational` aliases `PostgreSqlJsonb` for one release.
- Common services are Redis, migrator, API, gateway, Scheduler with embedded TickerQ, Blazor, React, and Uno WASM. Azure Functions remains Azure-only.
- Local search defaults to SQL and AI defaults to None. Live provider opt-ins must remain compatible with the selected lane.
- Container images use moving major or family tags from one catalog: SQL Server `2025-latest`, Service Bus emulator `latest` with SQL Server `2022-latest`, Azurite `latest`, Cosmos `vnext-latest`, pgvector PostgreSQL `pg18`, RabbitMQ `4-management`, SeaweedFS `latest`, MongoDB `8`, and Redis `8`.
- MinIO is replaced by SeaweedFS. Existing EF packages are validated first; an upstream request requires a focused reproduction and acceptance criteria.
- Azure and NonAzure deployments expose Blazor, React, and Uno through one gateway contract. React and Uno consume runtime `app-config.json` containing `gatewayBaseUrl`.
- Full lane and browser acceptance is manually dispatched; pull requests retain fast deterministic checks.
- The CI unit-test step must complete within 60 seconds. A slower run is a defect to isolate and fix, not an accepted baseline.

## Slice Status

Active delegated workers: 0. Queue order: S4, then S5 review.

| ID | Scope | Model | Why | Status | Branch | Worktree | PID | Thread | JSONL log | Final report | Worker handoff | Queue |
|---|---|---|---|---|---|---|---|---|---|---|---|---:|
| S0 | Binding `.scaffold` design decisions and vocabulary | gpt-5.6-luna through native agent transport | Explicit path list and documentation-only change; CLI 0.141.0 rejected luna and terra, so native same-model transport avoids a machine-wide upgrade | complete, reviewed, integrated through `d53d7f7` | docs/strict-hosting-lanes-design | `../scaffold-proof-lanes-design` | native agent | `/root/s0_lane_design`; failed CLI threads `01a0978c-7f8a-7972-a284-c9c1377a88f4`, `01a0978d-e463-70a2-8edd-c4a55a4efa4b` | not applicable for native transport | agent final message | `.tmp/orchestrated-refactor/s0-handoff.md` | 1 |
| S1 | Canonical hosting contract, strict lane validation, aliases, image catalog, unit/architecture tests | gpt-5.6-sol through native agent transport | Cross-layer configuration contract and compatibility behavior | complete, independently reviewed, integrated through `dc7bd9a` | feature/strict-lane-core | `../scaffold-proof-lane-core` | native agent | `/root/s1_lane_core`, reviewer `/root/s1_lane_core_review` | not applicable for native transport | agent final message | `.tmp/orchestrated-refactor/s1-handoff.md` | 2 |
| S2 | PostgreSQL JSONB default, MongoDB alternative, SeaweedFS and Data Protection provider changes with tests | gpt-5.6-sol through native agent transport | Migrations, persistence semantics, and provider boundaries | complete, independently reviewed, integrated through `51a274c` | feature/nonazure-data-providers | `../scaffold-proof-data-providers` | native agent | `/root/s2_data_providers`, reviewer `/root/s2_data_review` | not applicable for native transport | agent final message | `.tmp/orchestrated-refactor/s2-handoff.md` | 4 |
| S3 | React/Uno runtime config and Azure/Compose deployment parity | gpt-5.6-terra through native agent transport | Well-specified multi-file UI and deployment implementation | complete, independently reviewed, integrated through `954030a` | feature/lane-ui-deployment | `../scaffold-proof-ui-deployment` | native agent | `/root/s3_ui_deployment`, reviewer `/root/s3_deployment_review` | not applicable for native transport | agent final message | `.tmp/orchestrated-refactor/s3-handoff.md` | 3 |
| S4 | Aspire resource graphs, Testcontainers lane fixtures, CI manual matrix, and end-to-end topology tests | gpt-5.6-sol | Dependent architecture-sensitive integration and CI work | queued | feature/lane-orchestration-tests | `../scaffold-proof-lane-orchestration` | pending | pending | `.codex/scratch/strict-hosting-lanes-20260912/s4.jsonl` | `.codex/scratch/strict-hosting-lanes-20260912/s4.report.md` | `.tmp/orchestrated-refactor/s4-handoff.md` | 5 |
| S5 | Fresh-context integrated diff review and corrections | gpt-5.6-sol | Final cross-slice recall and regression gate | queued | feature/strict-hosting-lanes | main orchestration worktree | pending | pending | `.codex/scratch/strict-hosting-lanes-20260912/s5.jsonl` | `.codex/scratch/strict-hosting-lanes-20260912/s5.report.md` | not applicable | 6 |

## Integration and Verification

- Orchestrator reviews each real branch diff and accepts a slice only after its narrow checks pass.
- Independent slice commits are integrated onto `feature/strict-hosting-lanes`; S4 starts from the integrated S1-S3 base.
- Final checks cover solution and Uno builds, unit/architecture/endpoint tests, both component lanes, migrations, Aspire topology, Playwright UI parity, Compose validation, Bicep compilation, and affected frontend checks.
- Time the CI-equivalent unit command. If it exceeds 60 seconds, inspect category selection, container or live-service leakage, parallelization, hangs, and per-test duration before accepting the branch.
- `.scaffold/REFERENCE-STATUS.md` changes only after observed verification.

## Current State and Next Action

- Base: `origin/main` at `249c33674510523fd21d41655afcfeca61f0848a`.
- Orchestration branch: `feature/strict-hosting-lanes`.
- No Graphify graph exists.
- Headless Codex CLI 0.141.0 cannot run current 5.6 worker models; native model-matched agent transport is used without changing machine tooling.
- S2 review found and closed MongoDB index-before-consumer ordering and Data Protection discriminator compatibility defects before integration.
- Next: dispatch S4 from the combined S1-S3 implementation base and enforce the 60-second CI unit-test limit.
