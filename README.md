# Transaction Import Service

High-throughput backend that accepts large NDJSON transaction files, processes them asynchronously
(validate → normalize → fingerprint → risk-score → persist), and exposes import progress and
reconciliation results over HTTP.

| | |
| --- | --- |
| **Stack** | .NET 10 · ASP.NET Core · PostgreSQL 17 · Redis Streams · Docker Compose |
| **Assignment** | [`docs/Senior_Software_Engineer_Take_Home_Assignment.pdf`](docs/Senior_Software_Engineer_Take_Home_Assignment.pdf) |
| **Architecture** | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) · [ADRs](docs/adr/) |

---

## Table of contents

1. [Features](#features)
2. [How to run](#how-to-run)
3. [API endpoints](#api-endpoints)
4. [Typical workflow](#typical-workflow)
5. [Input format](#input-format)
6. [Local development](#local-development)
7. [Configuration](#configuration)
8. [Tests](#tests)
9. [CI / CD](#ci--cd)
10. [Repository layout](#repository-layout)
11. [Known limitations](#known-limitations)

---

## Features

### Required

| Area | What you get |
| --- | --- |
| **Streaming upload** | Multipart NDJSON upload; file is streamed to storage (never fully buffered). Size limits, content sniffing, SHA-256 while uploading. |
| **Async processing** | API returns `202 Accepted` immediately. A separate worker process parses, validates, scores and persists. |
| **Idempotent create** | Required `Idempotency-Key`. Replays return the same import; concurrent same-key uploads never create duplicates. |
| **Duplicate detection** | Unique `(providerId, transactionId)` in Postgres. Same fingerprint → `DUPLICATE_TRANSACTION`; different content → `DUPLICATE_CONFLICT`. Survives restarts and concurrent workers. |
| **Validation / normalization** | Per-line validation with stable rejection codes. Invalid lines do not abort the import. Documented normalization (trim, currency, UTC timestamps, NFC descriptions). |
| **Fingerprint + risk score** | Deterministic SHA-256 fingerprint; risk score 0–100 (low / medium / high) with a CPU-heavy component isolated in a bounded worker-thread pool. |
| **Progress & summary** | Live progress counters; reconciliation summary by currency, risk level, top merchants and accounts. |
| **Rejected records** | Cursor-paginated rejections with machine-readable codes and size-limited raw values (no sensitive dumps). |
| **Cancel / retry** | Soft cancel at batch boundaries; failed imports can be retried from the last checkpoint. |
| **Job reliability** | Transactional outbox → Redis Streams → DB lease. At-least-once delivery, effectively-once results via constraints. Stale-lease recovery + DLQ. |
| **Backpressure** | Bounded batch channel, bounded scoring queue, bounded concurrent imports. |
| **Health** | Separate `/health/live` (process) and `/health/ready` (Postgres + Redis + not shutting down). |
| **Metrics** | Prometheus `/metrics` (HTTP, imports, records, uploads, retries; low-cardinality labels only). |
| **Graceful shutdown** | Readiness goes unhealthy; in-flight batch finishes or job is released for resume. |
| **Security basics** | File-size limits, MIME/filename not trusted, path traversal blocked, injection-safe SQL, no stack traces in responses. Auth-ready design (`X-Provider-Id` → future principal). |
| **DI & layers** | Domain / Application / Infrastructure / Api / Worker. Architecture tests enforce dependency direction. |
| **Docs & ADRs** | README, ARCHITECTURE (Mermaid), four ADRs, logical git history. |

### Bonus / extras included

- Redis Streams queue with dead-letter stream
- Transactional outbox (no dual-write)
- OpenTelemetry metrics + optional OTLP tracing
- OpenAPI + Swagger UI
- S3-compatible and Cloudinary storage adapters (switch via config)
- Property-based tests (FsCheck) on the parser
- Testcontainers integration tests
- Data generator tool
- GitHub Actions CI (format / build / test) and CD (GHCR + SSH deploy)

---

## How to run

### Prerequisites

- Docker with Compose v2
- (Optional for local .NET runs) .NET SDK **10.0.301+** — pinned in `global.json`

### Start everything

```bash
cd transaction-import-service
docker compose up --build
```

This starts **PostgreSQL**, **Redis**, a one-shot **migrator**, then **api** and **worker**.

| Service | URL |
| --- | --- |
| API | http://localhost:8080 |
| Swagger UI | http://localhost:8080/swagger |
| OpenAPI document | http://localhost:8080/openapi/v1.json |
| API live / ready | http://localhost:8080/health/live · http://localhost:8080/health/ready |
| Worker ready | http://localhost:8081/health/ready |
| Prometheus metrics | http://localhost:8080/metrics |

Optional: copy `.env.example` → `.env` to change ports / DB password.

```bash
docker compose down       # stop
docker compose down -v    # stop and wipe DB + upload volumes
docker compose logs -f api worker
```

### Generate sample data

```bash
dotnet run --project tools/TransactionImport.DataGenerator -- \
  --records=10000 --output=data/sample.ndjson --duplicates=0.02 --invalid=0.01
```

| Flag | Default | Meaning |
| --- | --- | --- |
| `--records` | `10000` | Number of lines |
| `--output` | `data/sample.ndjson` | Output path |
| `--duplicates` | `0.02` | Fraction of duplicate lines |
| `--invalid` | `0.01` | Fraction of invalid lines |
| `--seed` | `42` | RNG seed (reproducible files) |

---

## API endpoints

Interactive docs: **http://localhost:8080/swagger**

All error responses use:

```json
{
  "error": {
    "code": "IMPORT_NOT_FOUND",
    "message": "The import does not exist.",
    "requestId": "..."
  }
}
```

Every response includes a server-generated `X-Request-Id` header (client-supplied ids are ignored).

### Imports

#### `POST /v1/imports` — create import

Upload an NDJSON file and start asynchronous processing.

| | |
| --- | --- |
| **Content-Type** | `multipart/form-data` (field name: `file`) |
| **Headers** | `Idempotency-Key` (required), `X-Provider-Id` (required) |
| **Success** | `202 Accepted` |

```bash
curl -sS -X POST http://localhost:8080/v1/imports \
  -H "Idempotency-Key: demo-$(uuidgen)" \
  -H "X-Provider-Id: provider-demo" \
  -F "file=@data/sample.ndjson;type=application/x-ndjson"
```

```json
{
  "id": "01a1ab0db9ca76c78786af5163264a8d",
  "status": "pending",
  "createdAt": "2026-11-05T09:32:45.906Z"
}
```

Common errors: `400` missing/invalid headers · `409` key still in progress · `413` file too large · `415` bad type / compressed upload.

#### `GET /v1/imports/{id}` — status

```bash
curl -sS http://localhost:8080/v1/imports/<id>
```

```json
{
  "id": "...",
  "status": "processing",
  "progress": {
    "processed": 58200,
    "accepted": 57010,
    "rejected": 1190,
    "duplicates": 214
  },
  "startedAt": "2026-07-24T08:01:00.000Z",
  "completedAt": null,
  "failureReason": null
}
```

**Statuses:** `pending` → `processing` → `completed` | `failed` | `cancelling` → `cancelled`

Progress invariant: `processed = accepted + rejected`, and `duplicates ≤ rejected` (duplicates are a kind of rejection).

#### `POST /v1/imports/{id}/cancel` — cancel

```bash
curl -sS -X POST http://localhost:8080/v1/imports/<id>/cancel
```

- Still `pending` → cancelled immediately.
- Already `processing` → moves to `cancelling`; worker stops at the next batch boundary and confirms `cancelled`.
- Batches already committed are **kept**.
- Completed / failed → `409 IMPORT_NOT_CANCELLABLE`.

#### `POST /v1/imports/{id}/retry` — retry failed import

```bash
curl -sS -X POST http://localhost:8080/v1/imports/<id>/retry
```

Only `failed` imports. Resets the attempt budget and re-queues; processing resumes from the last committed checkpoint.

#### `GET /v1/imports/{id}/summary` — reconciliation summary

```bash
curl -sS http://localhost:8080/v1/imports/<id>/summary
```

```json
{
  "importId": "...",
  "totals": { "accepted": 57010, "rejected": 1190, "duplicates": 214 },
  "byCurrency": [
    { "currency": "USD", "transactionCount": 25000, "totalAmount": 4350210.42 }
  ],
  "byRiskLevel": { "low": 41000, "medium": 14000, "high": 2010 },
  "topMerchants": [
    { "id": "merchant-18", "currency": "USD", "transactionCount": 100, "totalAmount": 12345.67 }
  ],
  "topAccounts": [ ... ],
  "isFinal": true
}
```

`isFinal: true` when the import finished and the snapshot was materialized; otherwise computed live from persisted rows.

#### `GET /v1/imports/{id}/rejections` — rejected records (cursor page)

| Query | Default | Notes |
| --- | --- | --- |
| `limit` | `50` | Max configurable (default ceiling 200) |
| `cursor` | — | Opaque keyset cursor from the previous page |

```bash
curl -sS "http://localhost:8080/v1/imports/<id>/rejections?limit=50"
curl -sS "http://localhost:8080/v1/imports/<id>/rejections?limit=50&cursor=<nextCursor>"
```

```json
{
  "items": [
    {
      "lineNumber": 125,
      "reason": "INVALID_CURRENCY",
      "message": "Currency must be a supported three-letter code",
      "rawValue": { "transactionId": "txn-100" }
    }
  ],
  "nextCursor": "..."
}
```

Selected rejection codes: `INVALID_JSON`, `MISSING_FIELD`, `INVALID_AMOUNT`, `INVALID_CURRENCY`, `INVALID_TIMESTAMP`, `DUPLICATE_TRANSACTION`, `DUPLICATE_CONFLICT`, `LINE_TOO_LONG`, …

### Health & observability

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health/live` | Process is alive (no dependency checks — safe for restart probes) |
| `GET` | `/health/ready` | Postgres + Redis reachable and service not shutting down |
| `GET` | `/metrics` | Prometheus text exposition |
| `GET` | `/swagger` | Swagger UI |
| `GET` | `/openapi/v1.json` | OpenAPI document |

Optional header on read endpoints: `X-Provider-Id` scopes results to that provider (create always requires it).

---

## Typical workflow

```bash
# 1) Generate data
dotnet run --project tools/TransactionImport.DataGenerator -- \
  --records=10000 --output=data/sample.ndjson

# 2) Upload
CREATE=$(curl -sS -X POST http://localhost:8080/v1/imports \
  -H "Idempotency-Key: demo-1" \
  -H "X-Provider-Id: provider-demo" \
  -F "file=@data/sample.ndjson;type=application/x-ndjson")
echo "$CREATE"
ID=$(echo "$CREATE" | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])")

# 3) Poll until terminal
watch -n1 "curl -sS http://localhost:8080/v1/imports/$ID"

# 4) Inspect results
curl -sS http://localhost:8080/v1/imports/$ID/summary | jq
curl -sS "http://localhost:8080/v1/imports/$ID/rejections?limit=20" | jq
```

### Processing pipeline (worker)

```text
claim job (DB lease)
  → stream file from checkpoint
  → parse / validate / normalize line-by-line
  → fingerprint
  → risk-score (bounded thread pool)
  → batch commit (rows + rejections + progress + checkpoint) in one DB transaction
  → complete / cancel / release / fail
```

---

## Input format

One JSON object per line (NDJSON):

```json
{
  "transactionId": "txn-10001",
  "accountId": "acc-201",
  "merchantId": "merchant-18",
  "amount": 145.75,
  "currency": "USD",
  "timestamp": "2026-07-20T10:25:00.000Z",
  "description": "Subscription payment"
}
```

| Field | Rules |
| --- | --- |
| `transactionId` | Required, unique per provider, 1–64 `[A-Za-z0-9._:-]` |
| `accountId` / `merchantId` | Required, same identifier rules |
| `amount` | Number &gt; 0, ≤ 4 decimal places |
| `currency` | 3-letter code from the supported allowlist |
| `timestamp` | ISO-8601 with offset/`Z`; converted to UTC |
| `description` | Optional, max 500 chars after normalization |

Empty lines are skipped. Bad lines become rejections; the import continues. Unexpected fields are ignored.

---

## Local development

Run dependencies in Docker, apps on the host:

```bash
docker compose up -d postgres redis
dotnet run --project src/TransactionImport.Migrator
dotnet run --project src/TransactionImport.Api       # http://localhost:5281
dotnet run --project src/TransactionImport.Worker    # http://localhost:5282
```

`appsettings.Development.json` targets `localhost:5432`, `localhost:6379`, and `./data/uploads`.

---

## Configuration

| Setting | Environment variable | Notes |
| --- | --- | --- |
| `Postgres:ConnectionString` | `Postgres__ConnectionString` | Required |
| `Redis:ConnectionString` | `Redis__ConnectionString` | Required |
| `FileStorage:Provider` | `FileStorage__Provider` | `local` (default), `s3`, or `cloudinary` |
| `FileStorage:LocalRoot` | `FileStorage__LocalRoot` | Default `/data/uploads` |
| `FileStorage:Bucket` / `ServiceUrl` / keys | `FileStorage__*` | For S3 / MinIO |
| `FileStorage:CloudName` / `ApiKey` / `ApiSecret` | `FileStorage__*` | Cloudinary dashboard → API Keys (free tier OK) |
| `FileStorage:CloudinaryFolder` | `FileStorage__CloudinaryFolder` | Optional folder prefix (default `transaction-import`) |
| `Imports:MaxFileBytes` | `Imports__MaxFileBytes` | Default 256 MiB |
| `Processing:BatchSize` | `Processing__BatchSize` | Default 1000 |
| `Processing:MaxConcurrentImports` | `Processing__MaxConcurrentImports` | Per worker process |
| `RiskScoring:WorkFactor` | `RiskScoring__WorkFactor` | PBKDF2 iterations (Compose demo default **50**; use ≥1000 for prod-like CPU) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | same | Enables OTLP trace export when set |

Compose also honors `API_PORT`, `WORKER_PORT`, `POSTGRES_*` from `.env`.

### Cloudinary (free tier)

1. Create a free account at [cloudinary.com](https://cloudinary.com/) and copy **Cloud name**, **API Key**, and **API Secret** from the dashboard.
2. Set `FileStorage__Provider=cloudinary` plus the three credentials (see `.env.example`). API and Worker must share the same settings.
3. Uploads are stored as Cloudinary **raw** assets under `CloudinaryFolder` (default `transaction-import`). Prefer S3 for very large production imports; free-tier quotas apply.

---

## Tests

```bash
dotnet format --verify-no-changes
dotnet test
```

- **Unit tests** — validation, normalization, fingerprint, risk model, status machine, upload inspection, create-import with fakes, FsCheck properties.
- **Integration tests** — real Postgres + Redis via Testcontainers: migrations, constraints, health, create/idempotency/concurrency/cancel.

Docker must be running for integration tests.

---

## CI / CD

| Workflow | Trigger | What it does |
| --- | --- | --- |
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | PR / push to `main` | Format → build → test |
| [`.github/workflows/cd.yml`](.github/workflows/cd.yml) | Push to `main` / tags / manual | Build api/worker/migrator → push GHCR → SSH → `compose pull && up -d` |

CD secrets (GitHub Environment **`production`**):

| Secret | Purpose |
| --- | --- |
| `DEPLOY_HOST` | Server hostname |
| `DEPLOY_USER` | SSH user |
| `DEPLOY_SSH_KEY` | Private key |
| `DEPLOY_PORT` | SSH port |
| `DEPLOY_COMPOSE_DIR` | Absolute path to the compose directory on the server |

On the server: `docker-compose.yml` + `docker-compose.prod.yml` and a `.env` with image names / DB password. CD exports `IMAGE_API`, `IMAGE_WORKER`, `IMAGE_MIGRATOR`.

---

## Repository layout

```text
src/
  TransactionImport.Domain/           entities, invariants
  TransactionImport.Application/      use cases + ports
  TransactionImport.Infrastructure/   Postgres, Redis, storage, scoring, OTel
  TransactionImport.Api/              HTTP composition root
  TransactionImport.Worker/           processing composition root
  TransactionImport.Migrator/         one-shot schema migrations
tests/
  TransactionImport.UnitTests/
  TransactionImport.IntegrationTests/
tools/
  TransactionImport.DataGenerator/
docs/
  ARCHITECTURE.md
  adr/                                0001–0004
  Senior_Software_Engineer_Take_Home_Assignment.pdf
```

---

## Known limitations

- Resumable (chunked) upload **HTTP** API is not exposed yet (storage/concat reader support exists).
- No authentication yet; designed so `X-Provider-Id` can move to the authenticated principal without a redesign.
- Outbox / rejected-record retention job not implemented.
- Fill `BENCHMARK.md` after a timed 500k run on target hardware.
- Compose sets `RiskScoring__WorkFactor=50` for snappy demos; raise for production-like load.
