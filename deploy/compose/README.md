# TaskFlow Portable lane - Docker Compose runbook

The Portable hosting lane (D-035, D-036) runs the whole app as containers on one VPS: Caddy terminates TLS
in front of the YARP gateway, PostgreSQL / RabbitMQ / S3 replace the Azure data services, and Azure is kept
only for Key Vault and App Configuration (D-044). These files are hand-written on purpose - the AppHost is
not published to Compose, so nothing here is generated and nothing here needs `Aspire.Hosting.Docker`.

| File | What it is |
|---|---|
| `docker-compose.yml` | The VPS stack: caddy, gateway, api, scheduler, blazor, migrator, redis, otel-lgtm, plus `pgbouncer` under profile `pooler` |
| `docker-compose.override.local.yml` | Containerised postgres / rabbitmq / minio and `build:` for the five app images, all under profile `local`. CI only |
| `Caddyfile` / `Caddyfile.local` | ACME TLS for `{$CADDY_DOMAIN}`, and the plain `:80` variant CI uses |
| `.env.example` | The environment contract - names only, no values |
| `images.env.example` | Shape of the digest-pinned image variables the deploy job writes |
| `pgbouncer/` | Transaction-pooling config for the opt-in `pooler` profile |

Infrastructure images (`otel-lgtm`, `pgbouncer`, `minio`) are pinned to a specific published tag rather than
`latest` (verified against the upstream registry 2026-09-09: `grafana/otel-lgtm:0.32.1`,
`edoburu/pgbouncer:v1.25.2-p0`, `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`). Only the five app images move
via digest through `images.env` on every deploy (see Rollback); bumping an infrastructure image tag is a
manual edit to `docker-compose.yml` / `docker-compose.override.local.yml`, done deliberately. Digest pinning
these three as well remains the production recommendation once the VPS path has run for real.

The `minio` image only appears in `docker-compose.override.local.yml` (CI/local, profile `local`) - the VPS
stack in `docker-compose.yml` has no `minio` service and expects `Storage:S3:*` to point at a real S3
endpoint. That matters because MinIO has not published a community-edition update since 2025-09 (the
project moved the community edition to maintenance mode); do not point a production deployment's S3 target
at this image or its successor - use a managed S3 provider or another actively maintained S3-compatible
server instead.

## First deploy

1. Point an A/AAAA record at the VPS. Caddy solves the ACME challenge itself, so DNS must resolve before
   the first `up`; nothing else in the stack publishes a port.
2. On the VPS, as the deploy user:
   ```bash
   mkdir -p ~/taskflow && cd ~/taskflow
   # deploy/compose/{docker-compose.yml,Caddyfile,pgbouncer/} are copied here by the deploy job
   cp .env.example .env && chmod 600 .env
   ```
3. Fill in `.env`. `TASKFLOW_LANE=Portable` seeds every provider default (S3, relational read model,
   relational audit, Redis data protection, RabbitMQ, PostgreSQL); set a `TASKFLOW_*_PROVIDER` only to
   deviate from it. Required in every deployment: the four `ConnectionStrings__*`, `ConnectionStrings__Redis1`,
   `ConnectionStrings__RabbitMq1`, the `Storage__S3__*` block, `CADDY_DOMAIN`, `ACME_EMAIL`,
   `Gateway__BaseUrl`, and the `AZURE_*` client credential that `DefaultAzureCredential` picks up through
   `EnvironmentCredential` for Key Vault and App Configuration.
   `Storage__S3__PublicServiceUrl` must be an address a **browser** can reach: SigV4 signs the Host header
   into a presigned download URL, so signing one against an in-network-only host hands the caller a URL it
   can never resolve.
4. Run `gh workflow run deploy-vps.yml -f operation=deploy -f commit_sha=<green sha>`. The job builds the
   five images, writes `images.env` with their digests, appends it into `.env`, then runs
   `docker compose pull && docker compose up -d --wait` and smokes `https://$CADDY_DOMAIN/healthz/ready`.

Manual equivalent, from `~/taskflow`:

```bash
docker compose pull
docker compose up -d --wait
curl -fsS "https://$CADDY_DOMAIN/healthz/ready"
```

## Why the app services have no healthcheck

The five app images are built on `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`. A chiseled image has
no shell and no `curl`, so there is nothing for `healthcheck:` to exec - a `CMD-SHELL` probe would fail
permanently and a `CMD` probe has no binary to call. Adding `curl` back to the runtime image to satisfy a
probe would trade the attack surface the chiseled base exists to remove for a status column.

Two mechanisms cover it instead, and both are stronger than a self-reported container healthcheck:

- **Caddy active upstream health checks.** `health_uri /healthz/ready` on both `reverse_proxy` blocks means
  an instance that fails readiness (database, outbox, scheduler, broker - D-049) is taken out of rotation by
  the component that actually routes traffic. The gateway does the same for the Api inside the stack
  (`ReverseProxy` active health, D-050).
- **`depends_on: migrator: service_completed_successfully`.** Schema changes have exactly one owner, and no
  app container starts until that owner exits 0 - the same single-migration-owner rule the AppHost enforces.

Infrastructure containers do have native probes (`pg_isready`, `redis-cli ping`, `rabbitmq-diagnostics`,
`mc ready`), which is what `--wait` and the `service_healthy` conditions above them key off.

## Connection pooling (D-045)

Off by default. To put PgBouncer in front of Postgres:

```bash
cp pgbouncer/userlist.txt.example pgbouncer/userlist.txt   # then paste the real SCRAM hash
docker compose --profile pooler up -d
```

Then repoint the four `ConnectionStrings__*` in `.env` at `pgbouncer:6432` and set
`Database__PostgreSql__PoolerMode=Transaction`. Both halves are required: the app side appends
`No Reset On Close=true;Max Auto Prepare=0` to the Npgsql string, and transaction pooling without it produces
"prepared statement already exists" failures under load. On Azure the same switch is the Flexible Server
`pgbouncer.enabled` parameter (`postgresPgBouncerEnabled` in `infra/main.bicep`), which is not offered on the
Burstable tier.

## Rotating secrets

Everything secret is either in `.env` (file mode 600, never committed, never baked into an image) or behind
an App Configuration Key Vault reference. To rotate:

1. Rotate at the source (Key Vault secret, Entra client secret, S3 access key, broker user).
2. Update the corresponding line in `.env` if the value is one the container reads directly.
3. `docker compose up -d --force-recreate <service>` for the affected services. Values that arrive through
   App Configuration are picked up by the sentinel-key refresh (D-042) without a restart.

The private NuGet feed credential is never in `.env`: it reaches the image build as a BuildKit secret
(`--secret id=nuget_credentials`) and leaves no layer behind.

## Rollback

`gh workflow run deploy-vps.yml -f operation=rollback`. The job reads the current release manifest artifact,
follows its `previousManifestArtifactId`, writes that manifest's digests back to `images.env`, re-appends it
into `.env`, and runs `docker compose up -d --wait` followed by the same smoke. No image is rebuilt and no
migration is re-run.

Application-only rollback: **database migrations stay forward-applied and must be backward compatible**, the
same contract as the Azure lane. A migration that drops a column cannot be rolled back this way.

Manual equivalent, if `images.env` from the previous release is still on the box:

```bash
cp images.env.previous images.env
cat .env.base images.env > .env
docker compose up -d --wait
```

## Logs and Grafana

```bash
docker compose logs -f --tail 200 api          # one service
docker compose logs --since 15m                # everything, recent
```

`otel-lgtm` (Grafana + Loki + Tempo + Mimir) is published to `127.0.0.1:3000` only - it has no authentication
worth exposing, so reach it over an ssh tunnel:

```bash
ssh -L 3000:127.0.0.1:3000 <user>@<vps>
# then open http://localhost:3000
```

Hosts export to it through `OTEL_EXPORTER_OTLP_ENDPOINT` (`http://otel-lgtm:4317`). Traces cross the broker:
the dispatcher injects W3C `traceparent` into message headers and the consumers link back to the producer
span (D-053), so a request through the gateway and out through RabbitMQ is one trace.

## Single-node ceiling, and the upgrade path

This stack is deliberately one node. What that costs:

- **No zero-downtime deploy.** `up -d` recreates containers in place; the window is seconds, not zero.
  Blue/green needs two full stacks behind Caddy with a switchover - out of scope here and not worth the
  complexity for a single node.
- **Blazor Server needs affinity above one replica.** A circuit is per-connection server state. With one
  replica the question does not arise; scale it out and Caddy needs a sticky upstream policy first (the
  Azure lane sets `stickySessions: 'sticky'` on that app for the same reason).
- **The node is the failure domain.** Redis, the broker connection and the edge all die with it.

When any of those starts to matter, the upgrade path is **k3s on the same box, then a second node** - not a
bigger compose file. k3s keeps the container images and the environment contract exactly as they are; what
changes is that Deployments replace `depends_on`, the readiness probes already exposed at `/healthz/ready`
become real readiness gates, and rolling updates replace recreate-in-place. Docker Swarm is the other option
and needs no new concepts, but it is effectively in maintenance. Either way, `.env` becomes a Secret and
`images.env` becomes the image tags in the manifests.

## Secrets the deploy workflow needs

Names only; set them in the repository's Actions secrets.

| Secret | Purpose |
|---|---|
| `VPS_HOST` | Hostname or IP the deploy job connects to |
| `VPS_USER` | SSH user that owns `~/taskflow` and can run `docker compose` |
| `VPS_SSH_KEY` | Private key for that user (no passphrase; deploy-only) |
| `VPS_KNOWN_HOSTS` | `ssh-keyscan` output for `VPS_HOST`, so the job never uses `StrictHostKeyChecking=no` |
| `CADDY_DOMAIN` | Public domain, used by the post-deploy smoke |
| `NUGET_PAT` | Existing secret; the private EF.* feed credential for the image build |
