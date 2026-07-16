# Performance and Capacity Envelope

ArrControl's v1 reference envelope is 20 enabled instances, 100,000 normalized catalog rows, an individual bulk operation of at most 10,000 targets, and a burst of 250 live-event reconnect handshakes at concurrency 50. This is a tested ceiling for the stated shape, not an unlimited-instance claim.

## Release targets

| Workload | Dataset/concurrency | Gate |
| --- | --- | --- |
| Cached missing projection | 100,000 missing rows across 20 instances; 200 requests at concurrency 20; page size 50 plus cursor lookahead | p95 below 500 ms; no failures |
| Broad operation snapshot | 10,000 distinct instance/key targets | durable creation below 15 seconds; all targets present |
| Live reconnect database handshake | 500 retained events; 250 reconnects at concurrency 50 | p95 below 500 ms; no lost/error response |

The 2026-07-16 reference run used PostgreSQL 17 Alpine with 256 MiB shared memory on an 8-logical-CPU, 23.47-GiB Debian Docker host. It measured 259.1 ms projection p95, 6,975.1 ms for 10,000-target creation, and 351.3 ms reconnect p95. The harness calls the production EF stores directly, so these figures isolate application/database behavior from TLS, reverse-proxy, and client-network latency. They do not promise the same latency on smaller hardware.

The initial run exposed a 3,685.2 ms projection p95. The `MissingSortIndex` migration adds the stored lowercase title key and keyset index; the passing result is the regression evidence for that schema change.

## Reproduction

The capacity test is opt-in and skipped by ordinary `dotnet test` runs:

```text
ARRCONTROL_RUN_PERFORMANCE_TESTS=1 dotnet test \
  tests/ArrControl.IntegrationTests/ArrControl.IntegrationTests.csproj \
  --filter FullyQualifiedName~PerformanceEnvelopeTests \
  --logger "console;verbosity=detailed"
```

It creates an isolated Testcontainers database, applies all migrations, bulk-seeds the exact dataset, warms the query, records every elapsed sample, enforces the thresholds, and deletes the container. Docker must provide at least 256 MiB PostgreSQL shared memory; baseline Compose configures that amount. A scheduled/manual workflow repeats the test independently of the fast pull-request suite.

## Operating envelope

- Start with one API/worker process and PostgreSQL 17. Keep scheduler concurrency at the default four until database and upstream latency show headroom.
- Reserve at least 256 MiB PostgreSQL shared memory. Monitor connections, CPU, memory, temporary-file use, lock waits, and query p95; total machine memory must also cover PostgreSQL data/cache and the Argon2 allowance documented in operations.
- Keep result/page bounds and polling defaults unchanged: missing pages at most 200, history at most 200 per read, provider snapshots at most 10,000, activity every 30 seconds, health every five minutes, and catalogs every 15 minutes.
- Scale PostgreSQL resources before adding API replicas when database CPU, I/O, or lock wait is the limiting signal. Replicas do not remove projection or operation-write load from PostgreSQL.
- Re-run the harness on the intended deployment class before exceeding any tested cardinality, changing indexes/query plans, raising scheduler concurrency, or claiming a smaller hardware baseline. Add an end-to-end TLS/reverse-proxy load run when a concrete production topology is known.

Capacity outside this table is unvalidated. Provider response time and upstream rate limits control synchronization throughput and are deliberately excluded from the cached-read envelope.
