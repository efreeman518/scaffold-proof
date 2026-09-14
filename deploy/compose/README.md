# TaskFlow NonAzure lane - Docker Compose runbook

The NonAzure hosting lane (D-060, D-036) runs the whole app as containers on one VPS: Caddy terminates TLS
in front of the YARP gateway, PostgreSQL / RabbitMQ / S3 replace Azure data services, and the lane has no
Azure service dependency. These files are hand-written on purpose - the AppHost is not published to Compose.

| File | What it is |
|---|---|
| `docker-compose.yml` | Canonical NonAzure stack: caddy, PostgreSQL, RabbitMQ, SeaweedFS, Redis, OpenObserve, migrator, gateway, api, scheduler, Blazor, React, Uno, and optional `pgbouncer` / `mongo` profiles |
| `docker-compose.override.local.yml` | `build:` for the app images under profile `local`; it excludes deployed OpenObserve because local development uses the Aspire Dashboard |
| `Caddyfile` / `Caddyfile.local` | ACME TLS for `{$CADDY_DOMAIN}`, and the plain `:80` variant CI uses |
| `.env.example` | Operator template with clearly marked non-production values; copy to `.env.base` and replace every `CHANGE_ME` value |
| `images.env.example` | Shape of the digest-pinned image variables the deploy job writes |
| `pgbouncer/` | Transaction-pooling config for the opt-in `pooler` profile |

The lane uses the centralized D-060 major/family tags: `pgvector/pgvector:pg18`, `rabbitmq:4-management`,
`chrislusf/seaweedfs:latest`, `mongo:8`, and `redis:8`, plus the D-061 pinned OpenObserve OSS image
`public.ecr.aws/zinclabs/openobserve:v1.0.0`. Only app images move via digest through `images.env`
on every deploy (see Rollback); infrastructure-image tag changes are deliberate edits to `docker-compose.yml`.
SeaweedFS exposes S3 internally at `seaweedfs:8333`; Caddy proxies its browser-facing hostname so presigned
URLs retain a reachable signed host without publishing the S3 port directly. S3 requests require SigV4, so
Caddy checks SeaweedFS readiness through the unauthenticated master endpoint `/cluster/healthz` on port 9333;
it never uses an unsigned request to the authenticated S3 root as a health probe.

## VPS prerequisites

Install Docker Engine with the Compose plugin, OpenSSH, `curl`, and GNU `base64` on the VPS. The deploy and
rollback gates use host `curl` to verify OpenObserve health, then run a digest-pinned OpenTelemetry
`telemetrygen` container to verify authenticated OTLP/gRPC log and trace ingestion. They do not add
diagnostic tools to the distroless OpenObserve container.

## Upgrade from the LGTM deployment

The previous deployment stored Grafana LGTM data in a Compose-labeled `lgtm-data` volume. Compose removes
the replaced container but deliberately leaves that volume behind. The deployment gate stops when it finds
one so old telemetry cannot silently consume disk and cannot be deleted without an operator decision.

On the VPS, inspect every reported volume. Back up any data that must be retained, verify the archive, then
explicitly remove only the reported legacy volume:

```bash
cd ~/taskflow
docker volume ls --filter label=com.docker.compose.project=taskflow \
  --filter label=com.docker.compose.volume=lgtm-data
docker volume inspect <reported-volume>
mkdir -p backups
docker run --rm -v <reported-volume>:/source:ro -v "$PWD/backups:/backup" alpine:3.22 \
  sh -c 'tar -czf /backup/lgtm-data.tgz -C /source .'
tar -tzf backups/lgtm-data.tgz > /dev/null
docker volume rm <reported-volume>
```

Remove any legacy `OTEL_EXPORTER_OTLP_ENDPOINT=` line from `.env.base`. The strict NonAzure lane owns
`http://openobserve:5081`; operator overrides are rejected so an old `http://otel-lgtm:4317` value cannot
silently route telemetry to the removed service.

## First deploy

1. Point an A/AAAA record at the VPS. Caddy solves the ACME challenge itself, so DNS must resolve before
   the first `up`; nothing else in the stack publishes a port.
2. From a trusted checkout, stage the OpenObserve bootstrap files and operator configuration source:
   ```bash
   ssh <user>@<vps> 'mkdir -p ~/taskflow && chmod 700 ~/taskflow'
   scp deploy/compose/docker-compose.yml <user>@<vps>:~/taskflow/
   scp deploy/compose/.env.example <user>@<vps>:~/taskflow/.env.base
   scp deploy/compose/images.env.example <user>@<vps>:~/taskflow/images.env.bootstrap
   ssh <user>@<vps> 'chmod 600 ~/taskflow/.env.base'
   ```
   Every deployment also refreshes `~/taskflow/.env.base.example`, so the current contract is available beside
   the operator-managed file without overwriting it.
3. Bootstrap OpenObserve before starting TaskFlow. In `.env.base`, first set real
   `OPENOBSERVE_ROOT_EMAIL` and `OPENOBSERVE_ROOT_PASSWORD` values. Keep `OPENOBSERVE_ORGANIZATION=default`
   and set `OPENOBSERVE_OTLP_BASIC_CREDENTIAL` to a temporary, base64-shaped interpolation value. Then start
   OpenObserve alone and wait for its host-only health endpoint:
   ```bash
   ssh <user>@<vps>
   cd ~/taskflow
   bootstrap_credential=$(printf '%s' 'default:o2oi_bootstrap' | base64 -w0)
   sed -i "s|^OPENOBSERVE_OTLP_BASIC_CREDENTIAL=.*|OPENOBSERVE_OTLP_BASIC_CREDENTIAL=$bootstrap_credential|" .env.base
   unset bootstrap_credential
   { cat .env.base; printf '\n'; cat images.env.bootstrap; } > .env
   chmod 600 .env
   docker compose up -d openobserve
   curl -fsS --retry 12 --retry-delay 5 --retry-all-errors --connect-timeout 5 --max-time 10 \
     http://127.0.0.1:5080/healthz > /dev/null
   ```
   From the trusted workstation, open `ssh -L 5080:127.0.0.1:5080 <user>@<vps>`, sign in at
   `http://localhost:5080` with the root account, then copy the automatically created default token or create
   a named token under IAM > Ingestion Tokens. OpenObserve ingestion tokens start with `o2oi_` and are limited
   to ingestion endpoints. Encode the organization identifier and token without a trailing newline and replace
   the temporary value without printing either credential:
   ```bash
   read -rsp 'OpenObserve o2oi_ ingestion token: ' openobserve_token; printf '\n'
   openobserve_basic=$(printf '%s' "default:$openobserve_token" | base64 -w0)
   sed -i "s|^OPENOBSERVE_OTLP_BASIC_CREDENTIAL=.*|OPENOBSERVE_OTLP_BASIC_CREDENTIAL=$openobserve_basic|" .env.base
   unset openobserve_token openobserve_basic
   ```
4. Fill in the rest of `.env.base` and replace every `CHANGE_ME` value. The checked-in D-060 contract fixes
   `Hosting__Lane=NonAzure`, PostgreSQL, RabbitMQ, S3,
   PostgreSQL JSONB, relational audit and Redis Data Protection. Do not change those lane-owned settings or
   add `TASKFLOW_*_IMAGE` or `OTEL_EXPORTER_OTLP_ENDPOINT` values; the workflow manages images and the strict
   lane owns its OpenObserve endpoint.
   Required values are `POSTGRES_*`, `REDIS_PASSWORD`, `RABBITMQ_DEFAULT_*`, the database connection strings,
   `ConnectionStrings__Redis1`, `Messaging__RabbitMq__ConnectionString`, `ConnectionStrings__RabbitMq1`, the
   `Storage__S3__*` block, both encryption keys, `OPENOBSERVE_ROOT_*`, `OPENOBSERVE_OTLP_BASIC_CREDENTIAL`,
   `CADDY_DOMAIN`, `S3_PUBLIC_DOMAIN`, `ACME_EMAIL`, `Gateway__BaseUrl`, `GATEWAY_BASE_URL`, and the three
   `*_UI_ORIGIN` / two `*_UI_DOMAIN` values. Set `MONGO_INITDB_ROOT_*` before using the optional Mongo profile.
   Set `Storage__S3__PublicServiceUrl` to
   `https://<S3_PUBLIC_DOMAIN>` so the browser can follow a presigned URL. The static React and Uno images
   write their minimal `/app-config.json` from `GATEWAY_BASE_URL` at container start, so one digest can target
   a changed gateway origin without a provider-specific UI build.
   `Storage__S3__PublicServiceUrl` must be an address a **browser** can reach: SigV4 signs the Host header
   into a presigned download URL, so signing one against an in-network-only host hands the caller a URL it
   can never resolve.
5. Run `gh workflow run deploy-vps.yml -f operation=deploy -f commit_sha=<green sha>`. The job builds the
   seven images, writes `images.env` with their digests, rebuilds generated `.env` from `.env.base` plus
   `images.env`, then runs
   `docker compose pull && docker compose up -d --wait` and smokes `https://$CADDY_DOMAIN/healthz/ready`.

Manual equivalent, from `~/taskflow`:

```bash
{ cat .env.base; printf '\n'; cat images.env; } > .env
chmod 600 .env
docker compose config -q
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

Infrastructure containers do have native probes (`pg_isready`, authenticated `redis-cli ping`,
authenticated `mongosh`, `rabbitmq-diagnostics`,
and SeaweedFS master `/cluster/healthz`), which is what `--wait` and the `service_healthy` conditions above them key off.
OpenObserve v1.0.0 is a distroless image with no built-in healthcheck and no shell, curl, or wget, so it follows
the same no-synthetic-probe rule as the chiseled app images. Verify its documented `/healthz` endpoint through
the loopback tunnel after startup when troubleshooting the observability service itself.

Compose separates the public edge from internal app, data, cache, and telemetry networks. Caddy and the static
UI containers cannot address the API, PostgreSQL, RabbitMQ, Redis, or MongoDB. Gateway and Blazor receive no
data-plane credentials; only API and Scheduler receive the database, broker, object-storage, cache, and optional
Mongo settings.

## Connection pooling (D-045)

Off by default. To put PgBouncer in front of Postgres, copy the credential template, repoint the four database
`ConnectionStrings__*` values in `.env.base` at `pgbouncer:6432`, and set
`Database__PostgreSql__PoolerMode=Transaction`:

```bash
cp pgbouncer/userlist.txt.example pgbouncer/userlist.txt   # then paste the real SCRAM hash
docker compose --profile pooler up -d
```

The workflow derives `COMPOSE_PROFILES=pooler` from that mode, waits for PgBouncer before running the migrator,
and preserves no profile when the mode is empty or `None`. Both the profile and connection changes are required:
the app side appends `No Reset On Close=true;Max Auto Prepare=0` to the Npgsql string, and transaction pooling
without it produces "prepared statement already exists" failures under load.

## MongoDB read-model alternative

PostgreSQL JSONB is the default NonAzure read model. To use MongoDB instead, set
`ReadModel__Provider=MongoDb`, replace `MONGO_INITDB_ROOT_USERNAME` and `MONGO_INITDB_ROOT_PASSWORD`, and set
`ConnectionStrings__MongoDb1=mongodb://<encoded-user>:<encoded-password>@mongo:27017/taskflow?authSource=admin`
in `.env.base`. The deploy and rollback workflows derive `mongo` from the read-model setting and `pooler` from
transaction pooler mode, producing no profile, either one, or `mongo,pooler` in generated `.env`. Every Compose
command using it, including pull, up, health waits, ps, logs, and rollback, selects the same topology.
`COMPOSE_PROFILES` must not be added to `.env.base`; the two provider settings remain the operator sources.

For a manual start before the workflow has generated `.env`, rebuild it and select the explicit profile:

```bash
docker compose --profile mongo up -d --wait
```

Do not enable the profile for the default PostgreSQL JSONB path.

## Rotating secrets

Everything secret is in operator-managed `.env.base` (file mode 600, never committed, never baked into an image).
Generated `.env` is deployment output and must never be edited directly. To rotate:

1. Rotate at the source (database password, S3 access key, broker user, or local encryption key).
2. Update the corresponding line in `.env.base`.
3. Run the deploy workflow again, or manually rebuild `.env` from `.env.base` and `images.env`, then run
   `docker compose up -d --force-recreate <service>` for the affected services.

The private NuGet feed credential is never in `.env`: it reaches the image build as a BuildKit secret
(`--secret id=nuget_credentials`) and leaves no layer behind.

## Rollback

`gh workflow run deploy-vps.yml -f operation=rollback`. The job reads the current release manifest artifact,
follows its `previousManifestArtifactId`, writes that manifest's digests back to `images.env`, combines them
with `.env.base` into generated `.env`, and runs `docker compose up -d --wait` followed by the same smoke. No
image is rebuilt and no migration is re-run.

Application-only rollback: **database migrations stay forward-applied and must be backward compatible**, the
same contract as the Azure lane. A migration that drops a column cannot be rolled back this way.

Manual equivalent, if `images.env` from the previous release is still on the box:

```bash
cp images.env.previous images.env
{ cat .env.base; printf '\n'; cat images.env; } > .env
docker compose up -d --wait
```

## Logs and traces

```bash
docker compose logs -f --tail 200 api          # one service
docker compose logs --since 15m                # everything, recent
```

Deployed NonAzure uses the AGPL OpenObserve OSS single-node image. Its data directory is the persistent
`openobserve-data` volume. `OPENOBSERVE_RETENTION_DAYS` defaults to 14 days and must remain at least 3 days;
`ZO_COMPACT_FAST_MODE=false` trades compaction speed for lower peak memory on the VPS. The UI is published to
loopback only, while OTLP gRPC port 5081 is not published to the host. Reach the UI over an SSH tunnel:

```bash
ssh -L 5080:127.0.0.1:5080 <user>@<vps>
# then open http://localhost:5080
```

`OPENOBSERVE_ROOT_EMAIL` and `OPENOBSERVE_ROOT_PASSWORD` bootstrap only the OpenObserve container. TaskFlow
hosts do not receive those plaintext values. They receive `OPENOBSERVE_OTLP_BASIC_CREDENTIAL`, the base64
encoding of `organization:o2oi_ingestion_token`, and Compose constructs OpenObserve's required OTLP headers:
`Authorization=Basic <credential>`, `organization`, and `stream-name`. Organization ingestion tokens are
accepted only by ingestion endpoints and survive user removal. Open-source service accounts have full access,
so this lane deliberately does not use one. Rotate the organization token when needed and replace only the
encoded credential.

Hosts export logs and traces over authenticated gRPC to `http://openobserve:5081`.
`OpenTelemetry__MetricsEnabled=false` is the deployed default because metrics create high-volume time series;
set it to `true` only for a bounded diagnosis. Traces cross the broker: the dispatcher injects W3C
`traceparent` into message headers, and the extracted producer context is the consumer span's parent (D-053),
preserving one contiguous trace from a gateway request through RabbitMQ processing.

After every deploy and rollback, the remote workflow waits for `http://127.0.0.1:5080/healthz`, then uses a
digest-pinned official `telemetrygen` image to export one log and one trace over the same authenticated OTLP
gRPC listener, organization, and stream configuration used by TaskFlow. Any failed export fails the operation.

No OpenTelemetry Collector is part of this minimal topology. Add one only when server-side tail sampling or
another processing stage becomes a measured requirement. For local development, run the Aspire AppHost and
use its Dashboard; the local Compose profile excludes OpenObserve and clears OTLP export settings.

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
| `REACT_UI_DOMAIN` | React static UI public domain, used by deploy and rollback smoke |
| `UNO_UI_DOMAIN` | Uno static UI public domain, used by deploy and rollback smoke |
| `NUGET_PAT` | Existing secret; the private EF.* feed credential for the image build |
