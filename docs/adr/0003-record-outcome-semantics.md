# ADR 0003: Record outcome semantics, provider identity and duplicates

- Status: Accepted
- Date: 2026-11-05

## Context

The brief leaves several behaviours undefined:

- Duplicates are defined as "same `transactionId` for the same provider", but neither the NDJSON record nor
  the upload request carries a provider.
- The status example has `processed = accepted + rejected` (58,200 = 57,010 + 1,190) while also listing
  `duplicates: 214`, so it is unclear which bucket duplicates belong to.
- Behaviour for the same `transactionId` with different content must be defined.

## Decision

**Provider identity.** `POST /v1/imports` requires an `X-Provider-Id` header (validated format, never taken
from file contents). When authentication is added, the provider will come from the authenticated principal
and the header will be removed. Provider identity is stored on the import, and all duplicate checks are
scoped to it.

**Progress arithmetic.** Every non-empty line ends in exactly one outcome:

- `processed = accepted + rejected`
- `duplicates <= rejected`: a duplicate is a rejected record with code `DUPLICATE_TRANSACTION` or
  `DUPLICATE_CONFLICT`.
- Empty lines are skipped and not counted.
- `byRiskLevel` totals equal `accepted`, because risk is only scored for accepted records.

These invariants are encoded in `ImportProgress` and covered by unit tests.

**Same id, same or different content.** A unique constraint on `(provider_id, transaction_id)` decides
which record is first, both within a file and across files, workers and restarts. When a later record hits
the constraint, its fingerprint (SHA-256 over normalized fields) is compared with the stored one:

- Equal fingerprint: `DUPLICATE_TRANSACTION`. The record is a harmless resubmission.
- Different fingerprint: `DUPLICATE_CONFLICT`. The record is a potential data-integrity problem worth
  investigating.

The originally accepted transaction is **never overwritten**.

**Cancellation keeps committed work.** Batches committed before cancellation stay accepted. The import ends
as `cancelled` with partial, consistent counters and summary. Re-importing the same file later reports those
records as `DUPLICATE_TRANSACTION`. Rolling back committed batches would need compensating deletes that race
with other imports' duplicate checks.

## Consequences

- Clients must send a provider id. This is documented in OpenAPI and enforced with a 400 error.
- Duplicate classification is deterministic regardless of worker interleaving, because the database
  constraint is the arbiter.
