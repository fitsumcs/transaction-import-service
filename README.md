# Transaction Import Service

A backend service that imports large NDJSON transaction files, processes them asynchronously
(validation, normalization, duplicate detection, fingerprinting, CPU-bound risk scoring) and
exposes import progress and reconciliation results over HTTP.

Built with **.NET 10**, **ASP.NET Core** and **PostgreSQL 17**. The assignment brief is in
[`docs/Senior_Software_Engineer_Take_Home_Assignment.pdf`](docs/Senior_Software_Engineer_Take_Home_Assignment.pdf).

## Status

| Area | State |
| --- | --- |
| Solution layout, dependency boundaries, architecture tests | Done |
| Domain: import lifecycle state machine, progress invariants, risk levels | Done |
| Hosting: composition roots, `/health/live`, `/health/ready`, error envelope, request ids, Swagger | Done |
| Docker Compose (api, worker, PostgreSQL) | Done |
| Database schema and migrations | Next |
| Import upload (`POST /v1/imports`) with idempotency | Planned |
| Streaming processing pipeline in the worker | Planned |
| Status, cancel, summary, rejections endpoints | Planned |
| Metrics, retry policy, graceful shutdown of in-flight batches | Planned |
| Data generator, load test, `BENCHMARK.md` | Planned |

## Quick start

Prerequisites: Docker with Compose v2.

```bash
docker compose up --build
```

| Service | URL |
| --- | --- |
| API | <http://localhost:8080> |
| Swagger UI | <http://localhost:8080/swagger> |
| OpenAPI document | <http://localhost:8080/openapi/v1.json> |
| API readiness | <http://localhost:8080/health/ready> |
| Worker readiness | <http://localhost:8081/health/ready> |

Default credentials are development-only. To override them, copy `.env.example` to `.env`.

## Running without Docker

Prerequisites: .NET SDK 10.0.301+ (pinned in `global.json`) and a PostgreSQL instance.

```bash
docker compose up -d postgres            # or point Postgres__ConnectionString at your own server
dotnet run --project src/TransactionImport.Api       # http://localhost:5281
dotnet run --project src/TransactionImport.Worker    # http://localhost:5282
```

`appsettings.Development.json` points both hosts at `localhost:5432`.

## Tests

```bash
dotnet test
```

- `tests/TransactionImport.UnitTests` has no external dependencies.
- `tests/TransactionImport.IntegrationTests` starts a disposable PostgreSQL container through
  [Testcontainers](https://dotnet.testcontainers.org/), so Docker must be running. Tests do not depend on
  execution order or a manually prepared database.

The test projects use xUnit v3 on Microsoft.Testing.Platform (enabled in `global.json`).

## Configuration

| Setting | Environment variable | Description |
| --- | --- | --- |
| `Postgres:ConnectionString` | `Postgres__ConnectionString` | Npgsql connection string. Required; startup fails fast when missing. |
| `ASPNETCORE_HTTP_PORTS` | same | Listening port inside containers (default `8080`). |

## Repository layout

```text
src/
  TransactionImport.Domain/          Entities, value objects, invariants. No dependencies.
  TransactionImport.Application/     Use cases and ports (interfaces). Depends on Domain only.
  TransactionImport.Infrastructure/  PostgreSQL, file storage, queue, health checks, DI registration.
  TransactionImport.Api/             HTTP host and composition root for request handling.
  TransactionImport.Worker/          Processing host and composition root for background work.
tests/
  TransactionImport.UnitTests/
  TransactionImport.IntegrationTests/
tools/
  TransactionImport.DataGenerator/   NDJSON test data generator.
docs/
  ARCHITECTURE.md
  adr/                               Architecture Decision Records.
```

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [ADR 0001: Technology stack](docs/adr/0001-technology-stack.md)
- [ADR 0002: Job execution model](docs/adr/0002-job-execution-model.md)
- [ADR 0003: Record outcome semantics](docs/adr/0003-record-outcome-semantics.md)
