# ADR 0002: Job execution model

- Status: Accepted
- Date: 2026-11-05

## Context

Imports must be processed asynchronously, survive process crashes and restarts, support concurrent
workers, allow cancellation, and never leave a job permanently locked. CPU-heavy risk scoring must not
affect API latency.

## Options considered

1. **In-process background queue in the API.** Simple, but scoring competes with request handling for CPU,
   and jobs are lost on crash.
2. **Redis/BullMQ or RabbitMQ.** Mature delivery semantics, but enqueueing is not atomic with creating the
   import row, which requires an outbox to avoid "import created but never queued" or the reverse.
3. **PostgreSQL-backed job queue consumed by a separate worker process.**

## Decision

Option 3.

- The API creates the import and its job **in the same database transaction** as the idempotency record.
  There is no dual write.
- Worker processes claim jobs with `SELECT ... FOR UPDATE SKIP LOCKED`, set a **lease** (`locked_by`,
  `lease_expires_at`) and renew it with a heartbeat. Expired leases are reclaimable by any worker, so a
  crashed worker never leaves a job stuck.
- Each processed batch commits accepted rows, rejected rows, progress counters and the **resume checkpoint**
  in one transaction. Re-delivery resumes from the last checkpoint, and `ON CONFLICT DO NOTHING` absorbs any
  overlap. Delivery is at-least-once, and **effectively-once** results come from idempotent writes and
  unique constraints.
- Attempts are counted per job. After the maximum is reached, the import is marked `failed` with a reason
  instead of retrying forever.
- The API and Worker are separate processes (separate containers) built from the same codebase, so
  scoring load cannot starve request threads.

## Consequences

- Queue throughput is bounded by PostgreSQL. This is acceptable because a job is an entire file (low job
  count, high work per job). Per-record work never goes through the queue.
- Workers poll with backoff, which adds up to one poll interval of pickup latency. `LISTEN/NOTIFY` can wake
  idle workers if this ever matters.
