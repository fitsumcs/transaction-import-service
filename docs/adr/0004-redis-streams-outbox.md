# ADR 0004: Redis Streams job queue with a transactional outbox

- Status: Accepted
- Date: 2026-11-05

## Context

ADR 0002 originally chose a PostgreSQL-backed job queue (`FOR UPDATE SKIP LOCKED`) to avoid a dual-write
between "import created" and "job enqueued". A real queue is still desirable for:

- Independent scaling of API and workers
- Built-in consumer groups and idle-message reclaim
- A dead-letter stream for poison messages
- Matching the assignment's bonus (Redis-backed queue + DLQ)

## Decision

**Redis Streams + Postgres transactional outbox.**

1. When an import is created (or released/retried), the same database transaction inserts an
   `outbox_messages` row.
2. `OutboxRelayHostedService` publishes unpublished rows with `XADD` and marks them published.
3. Workers consume with `XREADGROUP` / `XAUTOCLAIM`, process under a **database lease**, then `XACK`.
4. Poison / unparseable messages go to `imports:jobs:dlq`.

The Redis message is only a notification ("import X has work"). The database lease is the source of
truth for ownership, so duplicate or stale deliveries are harmless. If Redis loses data, stale-job
recovery re-enqueues pending imports whose `last_enqueued_at` is too old.

## Consequences

- Local stack needs Redis (added to Docker Compose).
- Publish is at-least-once; effectively-once results still come from unique constraints and lease-fenced
  batch commits (ADR 0002 / 0003).
- Outbox table grows; a retention job can prune published rows (bonus / follow-up).
