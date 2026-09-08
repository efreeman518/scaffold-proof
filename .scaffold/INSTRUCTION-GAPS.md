# INSTRUCTION-GAPS

- EF.Data couples core to SQL Server (MigrationSupport, NoLock interceptors), blocking a second relational provider without an app-local shim.
- No keyset/cursor paging primitive in EF.Data.Contracts; scaffolds default to offset+Total paging only.
- Scaffolded encryption pattern is Always Encrypted-only; no provider-neutral application-layer column encryption option.
- No PostgreSQL test fixture in EF.IntegrationTesting; only a SQL Server container fixture is provided.
- Outbox is declared in resource-implementation.yaml schema but never actually scaffolded (no table, no claim/drain code).
- EF.Storage usage guidance leads to a per-request CreateIfNotExists container check instead of one-time startup provisioning.
- Scaffolded scheduler jobs are log-only stubs with a fixed 500-row page and no real repository implementation.
- Scaffolded domain events publish post-commit best-effort with a random MessageId, with no inbox/idempotency guidance for consumers.
- No server-side page-size clamp scaffolded; a caller-supplied page size is passed through unbounded.
- ClientWins is the scaffolded concurrency default, which makes the framework's 409 concurrency-conflict mapping unreachable in practice.
- No portable/non-Azure hosting lane in the instruction set; every scaffolded provider is Azure-only with no S3/Postgres/OpenAI-compatible arm.
- Provider-switch anatomy (enum + config key + env var + resolver + dispatcher, env wins, unknown falls back to default) is not described as a reusable recipe; each scaffold session reinvents it.
- No runtime/GC profile guidance per host (Server GC vs Workstation GC, DATAS, TieredPGO, ReadyToRun) for the scaffolded host set.
- No source-generated JSON (`JsonSerializerContext`) guidance for scaffolded hosts; scaffolds default to reflection-based System.Text.Json everywhere.
- No health probe contract guidance (separate liveness vs readiness, what belongs in each); scaffolds emit one combined health endpoint.
- No trace-context propagation guidance for message brokers; a scaffolded consumer starts a disconnected trace instead of joining the producer's.
- No feature-flag guidance (dynamic flags, targeting, HTTP-surface gating vs consumer-side gating); scaffolds default to static env-var kill switches only.

