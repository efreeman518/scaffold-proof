# Adoption follow-ups - orchestration handoff

Durable state for fixing the follow-ups left by the EF.Packages 2.0 adoption (docs/plans/ef-packages-2.0-adoption.md)
and adopting the FlowEngine retry release. The next session resumes from this file alone.

- **Run id:** `followups-4e1efc`
- **Integration branch:** `fix/followups-4e1efc`, cut from main 209c828. Slices PR into it; those PRs run no CI.
  One final PR to main runs CI once and is the merge gate.
- **Authorization:** owner (2026-10-01): "start and implement to completion"; merge every PR in this effort once its
  tests pass. Copilot review is skipped. An EF.Packages main merge publishes packages and is covered.

## Slices

| Id | Repo | Scope | Depends | Model | Status | Branch / PR | Agent | Lease |
|---|---|---|---|---|---|---|---|---|
| P1 | proof | Scheduler `ExecuteInTransactionAsync` retry safety: clean tracker per attempt, counts after success, commit-landed case; transient-commit race tests on both providers | - | opus | merged #40 | removed | a31c3365 | released |
| P2 | proof | `If-Match: *` writes run inside `RetryOnConcurrencyAsync`, exhaustion 409; root and child PUT/PATCH/DELETE and Category/Tag/Attachment; race tests; atomic category delete | - | opus | merged #39 (76cfe24) | removed | afe86938 | released |
| FE | proof | FlowEngine retry release: bump; API honors `Idempotency-Key` on POST create and child adds (UUIDv5 id when body Id is null); workflow nodes get `idempotencyKeyHeader` and explicit `retryPolicy`; tests | - | opus | merged #42 | removed | a7cfe964 | released |
| P3 | proof | Throw-policy architecture scan covers Infrastructure.Repositories and the Scheduler host | P1 | sonnet | merged #41 | removed | a17a6b07 | released |
| E1 | EF.Packages | Hunt the one-off EF.Test.Unit failure: three real-clock or file races found and fixed | - | sonnet | merged EF.Packages #90, published 2.0.116 | removed | a51770ad | released |
| R | proof | Independent review findings: loop-node config and D-059, SQL Server binary collation on keys, Guid-normalized scopes, Guid.Empty body id, wildcard delete after a landed commit, batched purge and root check, set-based writes bump Version, host-path tests, Idempotency-Key on all creates and in OpenAPI | P1, P2, P3, FE | opus | running | fix/followups-4e1efc-r | a1b7724b | 3b5c91ac |
| S | scaffold-ai | Instruction follow-through: transaction retry shape, `If-Match: *` retry, idempotency header, explicit FlowEngine node retry policies; the scaffold repos keep one regenerated initial migration per DbContext | P1, P2, FE | sonnet | running (opus) | docs/followups-4e1efc-s | a63337e4 | fdf21a32 | | | |

## Decisions

- Owner (2026-10-01): the scaffold repos are the foundation for new apps and never carry migration history,
  permanently. Each DbContext keeps exactly one initial migration per provider (TaskFlow: `InitialCreate`), and a
  schema change regenerates it. FE squashes the TaskFlow migrations and adds a check that enforces this.
- FE `Idempotency-Key`: a key table maps (tenant, scope, key) to a v7 id committed before the create, so GR-17's
  v7 rule and the existing replay paths are unchanged.
- P3 also bumps EF.* to 2.0.116.
- When this run completes, delete this file and docs/plans/ef-packages-2.0-adoption.md (no history in the
  baseline), after moving any open item into its design decision.

## Next action

P1, P2, P3 and FE are merged into the integration branch (#39-#42). R (the review fixes) and S (scaffold-ai) are
running. When R merges: rerun the full gate, open the final PR to main (CI is the gate), then merge S after
validate-reference passes against the final branch. Upstream FlowEngine defects (Retry=null start failure,
AddDirectHttpClient at startup, GetWarnings casing, loop-body key and retry) went to the owner as prompts.
