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
| P1 | proof | Scheduler `ExecuteInTransactionAsync` retry safety: clean tracker per attempt, counts after success, commit-landed case; transient-commit race tests on both providers | - | opus | running | fix/followups-4e1efc-p1 | a31c3365 | 442b78a8 |
| P2 | proof | `If-Match: *` writes run inside `RetryOnConcurrencyAsync`, exhaustion 409; root and child PUT/PATCH/DELETE and Category/Tag/Attachment; race tests | - | opus | running | fix/followups-4e1efc-p2 | afe86938 | 23612eb4 |
| FE | proof | FlowEngine retry release: bump; API honors `Idempotency-Key` on POST create and child adds (UUIDv5 id when body Id is null); workflow nodes get `idempotencyKeyHeader` and explicit `retryPolicy`; tests | - | opus | running | fix/followups-4e1efc-fe | a7cfe964 | 879d4064 |
| P3 | proof | Throw-policy architecture scan covers Infrastructure.Repositories and the Scheduler host | P1 | sonnet | queued | | | |
| E1 | EF.Packages | Hunt the one-off EF.Test.Unit failure (40 runs, trx, half under load); fix or close with evidence | - | orchestrator | running (loop, no agent) | - | - | - |
| S | scaffold-ai | Instruction follow-through: transaction retry shape, `If-Match: *` retry, idempotency header, explicit FlowEngine node retry policies | P1, P2, FE | sonnet | queued | | | |

## Next action

P1, P2 and FE are running in worktrees `.tmp/worktrees/followups-4e1efc-<slice>`, with state in `.tmp/orchestrated-refactor/followups-4e1efc/<slice>`. The E1 loop logs to EF.Packages `.tmp/followups-4e1efc-e1`. When P1, P2 and FE are merged into the integration branch, run P3, one independent review
of P1+P2+FE, then the final PR. S runs after the proof slices merge.
