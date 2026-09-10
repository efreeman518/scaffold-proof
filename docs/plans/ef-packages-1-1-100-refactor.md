# EF.* 1.1.100 Adoption Refactor - Orchestration Handoff

Durable state for the orchestrated refactor. Sessions and agents are disposable; this doc plus `docs/plans/ef-package-requests.md` (the requests) and `docs/plans/ef-packages-1-1-100-catalog.md` (S0's request-to-API catalog) must be enough to resume.

## Goal

Adopt the published EF.* 1.1.100 and EF.FlowEngine.* 1.0.169 packages: pin them, delete every `// fallback:` site in favor of the package member, absorb the four breaking changes, correct the two flagged assumptions, and refresh the artifacts and docs that described app-local shapes. Integration branch `feature/ef-packages-1-1-100` from `main` 226bec9; slices merge into it; one PR to `main` at the end.

## Package agent feedback (2026-09-10, verbatim facts)

- Landed as requested: 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 21, 22, 23, 24, 25, 26, 27, 29, 30, 31, 32.
- Request 5 encryption types match the app shape exactly. Request 6 cursor types live in EF.Common.Contracts beside PagedResponse; codec and pager in EF.Data.Contracts; the token carries a tenant key and a schema-version byte and fails closed. Request 7 keeps the parameterless UpsertAsync and adds a FlexLabs-backed overload plus a range variant. Request 23's package moved as-is (tests split into the package repo's existing test projects).
- Solved differently: request 19 has no newGuidV7(); `LoopNodeConfig.IdAs` (default `currentItemId`) holds a deterministic UUIDv5 of instance id, node id and iteration index; loop bodies use `{{$.context.currentItemId}}`. Request 28 shipped in EF.AI (ServiceCollectionExtensions over EFChatClientSettings; enums gained OpenRouter, Ollama, vLLM) - delete the app factory. Request 17 README half was already done. Request 3 partial: EF.Data.SqlServer split shipped; EF.Data keeps SQL Server references until 2.0 (Microsoft.Data.SqlClient still resolves).
- Breaking: request 18 landed, so `IntegrationNodeConfig_HasNoHeadersProperty_PackageRequest18` fails by design - remove it and `FlowEngineIfMatchOverrideHandler`; workflow JSON needs no change. `AdHocSqlQueryClient` moved to EF.FlowEngine.Clients.SqlServer (add the package, change the using) - not referenced here as of the last inventory, S0 verifies. grpc-message trailers no longer carry exception text and IncludeLogDataInResponse defaults to false. EventHubProducerBase.SendBatchAsync throws after a failed publish (not atomic across batches) - Event Hubs is not used here, S0 verifies.
- Assumptions that do not hold: the Azure Table audit arm is not replay-idempotent (a replay recomputes recordedUtc, so the RowKey differs and duplicates). Native upsert writes the entity's own key, so client-generated UUIDv7 needs ValueGeneratedNever (EntityBaseConfiguration already does this; pin it with a test or comment).
- Limitations: EF.Audit.Data cannot run on SQLite (DateTimeOffset ordering untranslatable); versioned OpenAPI from EF.AspNetCore requires `app.MapOpenApi().WithDocumentPerVersion()` or every document URL returns 404 (document names unchanged).

## Decisions

- Package member over app-local copy in every case where the shape matches; where the package shape differs, adapt the app to the package (the package is the reusable pattern), never keep both.
- Deleting an app-local type that a `.scaffold` artifact or `docs/plans/scaffold-instructions-handoff.md` names requires the artifact/doc line to move to the package member in the same slice.
- Audit replay idempotency: derive recordedUtc and the RowKey from the audit message's own identity (message id and occurrence time), never from the consumer clock.
- Delivery: slices merge into `feature/ef-packages-1-1-100`; final gate (both DB lanes, RabbitMQ lane against the published package, Release build); PR to `main`; merge after CI green.

## Slice table

Worktrees live in `C:\Users\EbenFreeman\source\repos\scaffold-proof-wt\<slice>`. Base for every slice branch: `feature/ef-packages-1-1-100` at spawn time.

| id | scope | model | status | branch | worktree | agent id |
|---|---|---|---|---|---|---|
| S0 | Pins to 1.1.100 / 1.0.169, restore, Release build break list, request-to-API catalog (`docs/plans/ef-packages-1-1-100-catalog.md`) | sonnet | running | feature/ef-s0-pins | scaffold-proof-wt/ef-s0 | ab2cf46d26bbbe3a5 |

Later slices are defined from the catalog (data layer and encryption; contracts, cursor and HTTP filters; messaging, scheduler, cache, storage and audit; FlowEngine and AI; docs and artifacts).

## Session log

- 2026-09-10: package agent feedback received; integration branch created from main 226bec9; S0 spawned. Next action: slice from S0's catalog.
