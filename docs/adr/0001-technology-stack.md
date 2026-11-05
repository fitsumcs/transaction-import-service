# ADR 0001: Technology stack

- Status: Accepted
- Date: 2026-11-05

## Context

The service must stream files with hundreds of thousands of records, isolate CPU-heavy scoring from
latency-sensitive HTTP traffic, keep strong consistency for idempotency and duplicate detection, and run
locally without paid services.

## Decision

- **.NET 10 / ASP.NET Core (LTS).** Kestrel and `System.IO.Pipelines` give allocation-efficient streaming,
  `System.Threading.Channels` gives bounded producer/consumer queues with built-in backpressure, and the
  runtime is truly multi-threaded, so CPU-bound work can run on a bounded set of threads without a separate
  process per task. Built-in DI, options validation, health checks, `TimeProvider` and
  `System.Diagnostics.Metrics` cover cross-cutting needs without third-party frameworks.
- **PostgreSQL 17.** Unique constraints, `INSERT ... ON CONFLICT`, row-level locks with `SKIP LOCKED` and
  transactional DDL let the database enforce correctness (idempotency keys, duplicate transactions, job
  ownership) instead of application code.
- **Npgsql with hand-written SQL** (data access approach recorded separately once implemented). The hot path
  is batched inserts with conflict handling, which ORMs express poorly.
- **Layered projects**: Domain, Application (ports), Infrastructure (adapters), with two hosts (Api,
  Worker). Dependency direction is enforced by architecture tests.
- **xUnit v3 + Testcontainers** for tests against a real PostgreSQL instance.

## Consequences

- No message broker or Redis is required. One fewer moving part locally and in deployment; the trade-off is
  that the database also carries queue load (see ADR 0002).
- Raw SQL requires discipline: all queries are parameterized and covered by integration tests.
