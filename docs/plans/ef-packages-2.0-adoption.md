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

## Active agents: 0

## FIFO queue

a0, a1, a2, a3, a5, a4, a6, sa

## Slices

| Id | Scope | Depends | Model | Status | Branch / PR | Worktree | Agent id | Lease | State folder |
|---|---|---|---|---|---|---|---|---|---|
| a0 | Bump every EF.* to 2.0.111; fix every compile break and removed API (moved namespaces, deleted overloads, renamed packages such as EF.UI.Refit, fail-closed tenant filter wiring, AuditLog column migration); solution builds; tests green | - | opus | queued | | | | | |
| a1 | Data, domain, common, tenancy, audit: D items' proof adoption | a0 | opus | queued | | | | | |
| a2 | Messaging, outbox, inbox: M items' proof adoption | a0 | opus | queued | | | | | |
| a3 | Hosting, web, UI client: H and U items' proof adoption | a0 | opus | queued | | | | | |
| a4 | Auth, gateway, rate limiting, cache, scheduler, storage, gRPC, AI: S items' proof adoption | a0, a3 | opus | queued | | | | | |
| a5 | Testing: T items' proof adoption (LoadRunner, fixtures, architecture rules, AI fakes) | a0 | opus | queued | | | | | |
| a6 | Proof docs (DESIGN-DECISIONS, REFERENCE-STATUS, README, tech design where affected) and the full test matrix including Docker integration and the Aspire lanes | a1-a5 | opus | queued | | | | | |
| sa | scaffold-ai: support/ef-packages-reference.md, skills/package-dependencies.md, templates from "generate" to "reference EF.X", maintenance canaries | a6 | opus | queued | | | | | |

## Slice notes

None yet.

## Kept on disk

None yet.
