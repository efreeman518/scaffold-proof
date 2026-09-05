# Test.Load

Manual load lane. Nothing here runs in CI, and nothing here gates a merge.

## The 5,000 RPS gate is deployment-only

The scalability baseline states a 5,000 requests-per-second target. That number is **not** verifiable from
this repository and no test here claims to verify it:

- A developer machine or a CI runner shares CPU with the database, the broker, and the load generator itself,
  so the bottleneck measured is the harness, not the application.
- The target assumes the deployed topology - Container Apps with the scale rules in `infra/`, Hyperscale or
  Flexible Server read replicas, Azure Managed Redis, and a Service Bus or RabbitMQ broker sized for it.
  Testcontainers reproduces the interfaces, never the capacity.
- A number produced locally would be quoted later as if it meant something. That is worse than no number.

The gate is therefore run against a deployed environment, against the deployed API's public endpoint, from a
load generator outside the cluster, and its result is recorded with the environment and SKUs it was measured
on. Treat any local figure from this project as a smoke check that the endpoints respond under concurrency.

## What this project is for

Shape checks and regression smoke: that a route still answers under concurrent callers, that cursor paging
does not degrade with depth, and that a change did not introduce an obvious per-request cost. Run it by hand
against a local stack (`dotnet run --project src/Host/Aspire/AppHost`) and read the numbers as relative,
never absolute.

## Data

Bulk data comes from `tests/Test.Support/Fixtures/MillionRowTaskFixture.cs`, driven by
`tests/Test.Support/Fixtures/seed-million-rows.ps1`. The mix is tenant-skewed with roughly 8% overdue, 3%
recurring templates, and 5% cancelled past the stale-cleanup window, so the scheduler jobs and the export
stream meet a realistic distribution rather than uniform rows. The integration lane uses the same fixture at
a reduced row count, which is what keeps the generator itself honest.
