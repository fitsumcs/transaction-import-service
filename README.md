# Transaction Import Service

A production-oriented backend that imports large NDJSON transaction files, processes them
asynchronously (validate → normalize → fingerprint → risk-score → persist), and exposes progress
and reconciliation results over HTTP.

**Stack:** .NET 10 · ASP.NET Core · PostgreSQL 17 · Redis Streams · Docker Compose

Assignment brief: [`docs/Senior_Software_Engineer_Take_Home_Assignment.pdf`](docs/Senior_Software_Engineer_Take_Home_Assignment.pdf)

---

## Quick start (recommended)

```bash
docker compose up --build
```

| Service | URL |
| --- | --- |
| API | http://localhost:8080 |
| Swagger UI | http://localhost:8080/swagger |
| OpenAPI | http://localhost:8080/openapi/v1.json |
| API readiness | http://localhost:8080/health/ready |
| Worker readiness | http://localhost:8081/health/ready |
| Prometheus metrics | http://localhost:8080/metrics |

Copy `.env.example` to `.env` to override passwords and ports. Defaults are for local demos only.

Stop with `docker compose down`. Add `-v` to wipe the database volume.

---

## Operate the app

### 1. Generate a test file

```bash
dotnet run --project tools/TransactionImport.DataGenerator -- \
  --records=10000 --output=data/sample.ndjson --duplicates=0.02 --invalid=0.01
```

For a larger file (benchmark-sized):

```bash
dotnet run --project tools/TransactionImport.DataGenerator -- \
  --records=500000 --output=data/large.ndjson
```

### 2. Upload an import

```bash
curl -sS -X POST http://localhost:8080/v1/imports \
  -H "Idempotency-Key: demo-$(uuidgen)" \
  -H "X-Provider-Id: provider-demo" \
  -F "file=@data/sample.ndjson;type=application/x-ndjson"
```

Response (202 Accepted):

```json
{ "id": "...", "status": "pending", "createdAt": "..." }
```

Required headers:

| Header | Purpose |
| --- | --- |
| `Idempotency-Key` | 1–128 safe characters. Replays return the same import; concurrent uploads with the same key do not create duplicates. |
| `X-Provider-Id` | Provider scope for duplicate detection (see [ADR 0003](docs/adr/0003-record-outcome-semantics.md)). |

### 3. Poll status

```bash
curl -sS http://localhost:8080/v1/imports/<id>
```

Statuses: `pending` → `processing` → `completed` | `failed` | `cancelling` → `cancelled`.

### 4. Summary and rejections

```bash
curl -sS http://localhost:8080/v1/imports/<id>/summary
curl -sS "http://localhost:8080/v1/imports/<id>/rejections?limit=50"
# next page:
curl -sS "http://localhost:8080/v1/imports/<id>/rejections?limit=50&cursor=<nextCursor>"
```

### 5. Cancel / retry

```bash
curl -sS -X POST http://localhost:8080/v1/imports/<id>/cancel
curl -sS -X POST http://localhost:8080/v1/imports/<id>/retry   # failed imports only
```

Cancellation stops at the next batch boundary; committed batches are kept. Retry resumes from the last checkpoint.

### 6. Health and metrics

```bash
curl -sS http://localhost:8080/health/live    # process alive (no dependency checks)
curl -sS http://localhost:8080/health/ready   # Postgres + Redis + not shutting down
curl -sS http://localhost:8080/metrics        # Prometheus text
```

---

## Architecture at a glance

```text
Client ──multipart──► API ──stream──► File storage
                        │
                        └──trx──► Postgres (import + idempotency + outbox)
                                        │
                              Outbox relay ──XADD──► Redis Streams
                                                        │
                                              Worker (XREADGROUP)
                                                        │
                              claim lease ──► parse/score/commit batches ──► Postgres
```

- **Why Redis?** Notifications and DLQ; see [ADR 0004](docs/adr/0004-redis-streams-outbox.md).
- **Why an outbox?** Creating the import and enqueueing are one database transaction — no dual-write hole.
- **Why a DB lease?** Ownership survives Redis redelivery; batch commits are fenced by `lease_token`.

Docs: [ARCHITECTURE.md](docs/ARCHITECTURE.md) · [ADRs](docs/adr/)

---

## Local development (without rebuilding images)

Prerequisites: .NET SDK 10.0.301+ (see `global.json`), Docker.

```bash
docker compose up -d postgres redis
dotnet run --project src/TransactionImport.Migrator
dotnet run --project src/TransactionImport.Api       # :5281
dotnet run --project src/TransactionImport.Worker    # :5282
```

`appsettings.Development.json` points at `localhost:5432` / `localhost:6379` and `./data/uploads`.

### Tests

```bash
dotnet format --verify-no-changes
dotnet test
```

Integration tests start PostgreSQL and Redis via Testcontainers (Docker required).

### Configuration

| Setting | Env var | Notes |
| --- | --- | --- |
| `Postgres:ConnectionString` | `Postgres__ConnectionString` | Required |
| `Redis:ConnectionString` | `Redis__ConnectionString` | Required |
| `FileStorage:Provider` | `FileStorage__Provider` | `local` or `s3` |
| `FileStorage:LocalRoot` | `FileStorage__LocalRoot` | Default `/data/uploads` |
| `Imports:MaxFileBytes` | `Imports__MaxFileBytes` | Default 256 MiB |
| `RiskScoring:WorkFactor` | `RiskScoring__WorkFactor` | PBKDF2 iterations (demo default 50 in Compose) |
| `Processing:BatchSize` | `Processing__BatchSize` | Default 1000 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | same | Enables OTLP trace export when set |

---

## CI / CD

- **CI** ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)): format check → build → test on every PR/push to `main`.
- **CD** ([`.github/workflows/cd.yml`](.github/workflows/cd.yml)): build api/worker/migrator images, push to GHCR, SSH to the server, `docker compose pull && up -d`.

CD secrets (GitHub → Environment `production`):

| Secret | Purpose |
| --- | --- |
| `DEPLOY_HOST` | Server hostname |
| `DEPLOY_USER` | SSH user |
| `DEPLOY_SSH_KEY` | Private key |
| `DEPLOY_PORT` | SSH port (default 22 if unset in the action input — set explicitly) |
| `DEPLOY_COMPOSE_DIR` | Absolute path to the compose directory on the server |

On the server, keep `docker-compose.yml` + `docker-compose.prod.yml` and a `.env` with image names / DB password. The CD job exports `IMAGE_API`, `IMAGE_WORKER`, `IMAGE_MIGRATOR`.

---

## Repository layout

```text
src/
  TransactionImport.Domain/
  TransactionImport.Application/     use cases + ports
  TransactionImport.Infrastructure/  Postgres, Redis, storage, scoring, OTel
  TransactionImport.Api/
  TransactionImport.Worker/
  TransactionImport.Migrator/
tests/
tools/TransactionImport.DataGenerator/
docs/  ARCHITECTURE.md, adr/, assignment PDF
```

---

## Known limitations / follow-ups

- Resumable (chunked) uploads: storage keys and `ConcatenatedReadStream` are ready; the HTTP protocol surface is not exposed yet.
- No auth yet; designed so `X-Provider-Id` can move to the authenticated principal without restructuring.
- Outbox / rejected-record retention job not implemented.
- Benchmark report (`BENCHMARK.md`) should be filled after a timed 500k run on target hardware.
- `RiskScoring__WorkFactor` defaults to 50 in Compose for snappy demos; use ≥1000 for production-like CPU load.
