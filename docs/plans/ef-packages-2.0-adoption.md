# EF.Packages 2.0 adoption - orchestration handoff

Durable state for adopting EF.Packages **2.0.111** in TaskFlow, then updating the scaffold-ai instruction set. The next
session resumes from this file alone.

- **Run id:** `adopt2-f8ca68`
- **Goal:** replace every app-local copy that 2.0 now ships as a package, bump every EF.* reference to 2.0.111, keep
  the full test matrix green, update the proof docs, then update the scaffold-ai templates and package references.
- **Source of truth for what shipped:** EF.Packages `docs/plans/2.0-handback.md` (per-item status, exact public
  names, breaking changes, config keys, adoption notes). Per-item "Proof adoption" steps live in the area specs,
  EF.Packages `docs/plans/2.0/specs/{D,H,M,S,T,U}-*.md`. Read them from `C:\Users\EbenFreeman\source\repos\EF.Packages`
  (main, read-only).
- **Orchestrator root:** `C:\Users\EbenFreeman\source\repos\scaffold-proof`.

## Decisions

1. Integration branch `integration/ef2-adoption`, cut from `main`. Slices PR into it; those PRs run no CI (ci.yml
   triggers only on PRs to main/develop). One final PR `integration/ef2-adoption` -> `main` runs CI once and is the
   merge gate.
2. Slice merge gate (into the integration branch): orchestrator diff review, `dotnet build TaskFlow.slnx -c Release`
   0 errors / 0 warnings, the unit, architecture and endpoint test projects green, and container tests for the areas
   the slice touched.
3. Workers use manual worktrees at `.tmp/worktrees/adopt2-f8ca68-<slice>` (the PreToolUse hook is bare
   `rtk hook claude`).
4. Owner directive: pragmatic. 2.0 is the baseline, with no history and no backwards compatibility. Delete the app
   copy when the package covers it, and prove each package member is used before deleting the local copy.
5. Models: opus for every proof slice (build, dependency, cross-file). The scaffold-ai slice is opus too (template
   and instruction correctness across many files).

## Next action

a0 (bump and compile) runs first. When it merges: a1, a2, a3 and a5 run in parallel; a4 runs after a3; a6 (docs and
the full matrix) runs after all of them; then sa (scaffold-ai).

## Active agents: 2

## FIFO queue

a4 (after a3), a6, sa

## Slices

| Id | Scope | Depends | Model | Status | Branch / PR | Worktree | Agent id | Lease | State folder |
|---|---|---|---|---|---|---|---|---|---|
| a0 | Bump every EF.* to 2.0.111; fix every compile break and removed API (moved namespaces, deleted overloads, renamed packages such as EF.UI.Refit, fail-closed tenant filter wiring, AuditLog column migration); solution builds; tests green | - | opus | merged | #29 (248c897) | removed | ada9b31b | released | removed |
| a1 | Data, domain, common, tenancy, audit: D items' proof adoption | a0 | opus | merged | #31 (e08dfa0) | removed | a86cdfed | released | removed |
| a2 | Messaging, outbox, inbox: M items' proof adoption | a0 | opus | running | `refactor/adopt2-f8ca68-a2` | `.tmp/worktrees/adopt2-f8ca68-a2` | a5a367d1 | 6d3bbc4f | `.tmp/orchestrated-refactor/adopt2-f8ca68/a2` |
| a3 | Hosting, web, UI client: H and U items' proof adoption | a0 | opus | rebasing onto a5 + narrowing ArgumentException mapping | `refactor/adopt2-f8ca68-a3` | `.tmp/worktrees/adopt2-f8ca68-a3` | a5993a86 | 5822fc34 | `.tmp/orchestrated-refactor/adopt2-f8ca68/a3` |
| a4 | Auth, gateway, rate limiting, cache, scheduler, storage, gRPC, AI: S items' proof adoption | a0, a3 | opus | queued | | | | | |
| a5 | Testing: T items' proof adoption (LoadRunner, fixtures, architecture rules, AI fakes) | a0 | opus | merged | #30 (43a39d7) | removed | ac9178d3 | released | removed |
| a6 | Proof docs (DESIGN-DECISIONS, REFERENCE-STATUS, README, tech design where affected) and the full test matrix including Docker integration and the Aspire lanes | a1-a5 | opus | queued | | | | | |
| sa | scaffold-ai: support/ef-packages-reference.md, skills/package-dependencies.md, templates from "generate" to "reference EF.X", maintenance canaries | a6 | opus | queued | | | | | |

## Slice notes

- a0 (#29): all EF.* on 2.0.111; compile breaks fixed. Local copies whose names collide with 2.0 types are kept
  behind file-level `using` aliases until their area slice deletes them. Fixed a surfaced bug: internal-bus handlers
  were registered from a disposed scope, so relational audit writes failed; registration now uses
  `AutoRegisterHandlers`. Tenant rule `AllowsAllTenants` = no tenant plus the System or GlobalAdmin role (fail-closed
  otherwise). Migration `AuditLogStartedAtUtc` (both providers, backfilled from `RecordedUtc`). Behavior change: a
  tenant-less, non-admin caller now reads nothing. ProblemDetails now use the 2.0 defaults (a3 reviews the wire
  contract). Baseline after a0: Unit 689, Architecture 80, Endpoints 189, Integration NonAzure 81 passed / 6 skipped,
  Azure 68 passed / 19 skipped.

- a5 (#30): all 19 T items adopted. New architecture rule: one public type per file over src/, with 51 existing
  offenders ratcheted. For a6: D-062 still names tests/Test.Support/LoadRunner.cs; readiness now requires a 2xx.

- a1: EF.Tenancy has one cross-tenant role (`TenancyOptions.GlobalAdminRole`), so the no-request system identity
  carries `[System, GlobalAdmin]`. a3's H14 `AddHttpRequestContext` gives the system context a single `SystemRole`;
  whichever merges second must keep GlobalAdmin on the system identity, or background jobs fail the tenant boundary.
  **EF.Packages follow-up (2.0.x):** EF.Tenancy should accept a list of cross-tenant roles, and H14 should accept a
  list of system roles.
- a3: sent back because mapping every framework `ArgumentException` to 400 hides server bugs (R14); map only
  app client-input types.

## Kept on disk

None yet.
