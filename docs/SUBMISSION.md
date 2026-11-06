# Submission

## Summary

High-throughput transaction import service in **.NET 10 / ASP.NET Core** with **PostgreSQL 17**
and **Redis Streams**. Multipart NDJSON uploads stream to storage; a separate Worker process validates,
normalizes, fingerprints, risk-scores, and persists batches under database leases. Progress, summary,
and cursor-paginated rejections are exposed over HTTP.

Repository: use the Git remote URL for this project.

**Live demo:** `https://abzlab-import.duckdns.org` · Swagger: `https://abzlab-import.duckdns.org/swagger`  
(How to provision: [`DEPLOY.md`](DEPLOY.md).)

**For evaluators:** follow [`REVIEWER_TEST_GUIDE.md`](REVIEWER_TEST_GUIDE.md) (Swagger steps, sample NDJSON,
expected statuses, cancel/idempotency/error checks).

## Architecture overview

- **API** — upload, status, cancel, retry, summary, rejections, health, metrics, OpenAPI.
- **Worker** — outbox relay, Redis consumer, lease heartbeats, bounded scoring pool, stale recovery.
- **Postgres** — correctness (idempotency, duplicates, leases, outbox).
- **Redis Streams** — job notifications + DLQ (not the source of truth for ownership).
- Layers: Domain → Application (ports) → Infrastructure (adapters); hosts are composition roots.

See [`ARCHITECTURE.md`](ARCHITECTURE.md) and [`adr/`](adr/).

## Important decisions

| Decision | Why |
| --- | --- |
| Separate API and Worker processes | CPU-heavy scoring must not starve HTTP ([ADR 0002](adr/0002-job-execution-model.md)) |
| Outbox + Redis Streams | Atomic create/enqueue without dual-write; bonus queue + DLQ ([ADR 0004](adr/0004-redis-streams-outbox.md)) |
| Unique `(provider, transactionId)` | Durable duplicate detection across workers/restarts ([ADR 0003](adr/0003-record-outcome-semantics.md)) |
| Hand-written SQL (Npgsql) | Batched inserts + conflict handling; DB as correctness engine ([ADR 0001](adr/0001-technology-stack.md)) |
| `IFileStorage` adapters | Local default; S3 and Cloudinary optional |

## Trade-offs

- Redis adds an operational dependency vs a DB-only queue; chosen for scaling and DLQ clarity.
- Cloudinary free tier is fine for demos; large imports should use local/S3.
- Compose demo uses low `RiskScoring__WorkFactor` for snappy demos; raise for production-like CPU.

## What I would improve with more time

- Chunked resumable upload HTTP API (storage concat already exists).
- AuthN/AuthZ replacing `X-Provider-Id`.
- Outbox / rejection retention job.
- Dynamic worker-pool sizing and per-provider rate limits.
- Kubernetes manifests and continuous performance regression in CI.

## Known risks / incomplete (intentional)

- No authentication (assignment optional; auth-ready header seam).
- Resumable **upload** HTTP not exposed.
- Data-retention policy not implemented.
- Public URL is set after deploy (`https://abzlab-import.duckdns.org` until `./deploy/scripts/update-readme-url.sh` is run).

## Benchmark summary

See [`BENCHMARK.md`](BENCHMARK.md) for measured results, method, bottlenecks, and how to reproduce.

## Tests

```bash
dotnet test
```

Includes unit tests (validation, risk, retry policy, fakes), property tests, Testcontainers integration
(idempotency, concurrency, schema), and **end-to-end** API+Worker flows (complete → summary →
rejections pagination, retry, stale-lease recovery).
