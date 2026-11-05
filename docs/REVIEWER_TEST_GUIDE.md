# Reviewer test guide

Use this checklist against the **deployed** API. Replace `https://YOUR_HOST` with the base URL
you were given (no trailing slash).

| Entry point | URL |
| --- | --- |
| Swagger UI | `https://YOUR_HOST/swagger` |
| OpenAPI JSON | `https://YOUR_HOST/openapi/v1.json` |
| Liveness | `https://YOUR_HOST/health/live` |
| Readiness | `https://YOUR_HOST/health/ready` |
| Prometheus metrics | `https://YOUR_HOST/metrics` |

**Required headers for imports**

| Header | Example | Notes |
| --- | --- | --- |
| `Idempotency-Key` | `review-001` | Required on `POST /v1/imports`. Reuse to test idempotency. |
| `X-Provider-Id` | `reviewer` | Required. Scopes duplicate detection and reads. |

Sample file (also in-repo): [`fixtures/reviewer-sample.ndjson`](fixtures/reviewer-sample.ndjson)

```text
{"transactionId":"rev-10001","accountId":"acc-201","merchantId":"merchant-18","amount":145.75,"currency":"USD","timestamp":"2026-07-20T10:25:00.000Z","description":"Subscription payment"}
{"transactionId":"rev-10001","accountId":"acc-201","merchantId":"merchant-18","amount":145.75,"currency":"USD","timestamp":"2026-07-20T10:25:00.000Z","description":"Subscription payment"}
{"transactionId":"rev-10002","accountId":"acc-202","merchantId":"merchant-9","amount":20,"currency":"EUR","timestamp":"2026-07-20T11:00:00.000Z"}
{"transactionId":"bad","accountId":"a","merchantId":"m","amount":-1,"currency":"USD","timestamp":"2026-07-20T10:25:00.000Z"}
{not-json
```

Save as `reviewer-sample.ndjson` (5 lines: 2 valid unique, 1 duplicate of the first, 1 invalid amount, 1 invalid JSON).

---

## 0. Smoke checks (1 minute)

1. Open `https://YOUR_HOST/swagger` — UI loads.
2. Open `https://YOUR_HOST/health/ready` — `"status":"Healthy"` (postgres + redis).
3. Open `https://YOUR_HOST/metrics` — Prometheus text (not HTML error).

---

## 1. Happy path via Swagger

### 1.1 Create import — `POST /v1/imports`

1. In Swagger, open **POST /v1/imports** → **Try it out**.
2. Set header `Idempotency-Key` = `review-001` (or any new unique value).
3. Set header `X-Provider-Id` = `reviewer`.
4. Upload `reviewer-sample.ndjson` in the `file` field.
5. **Execute**.

**Expect:** `202 Accepted`

```json
{ "id": "<32-hex>", "status": "pending", "createdAt": "..." }
```

Copy `id` for later steps.

### 1.2 Idempotent replay

1. Repeat the same request with the **same** `Idempotency-Key` and `X-Provider-Id` (same or any file).
2. **Expect:** `202` with the **same** `id` (no second import).

### 1.3 Poll status — `GET /v1/imports/{id}`

1. Call **GET /v1/imports/{id}** with the id from 1.1.
2. Refresh a few times.

**Expect:** `pending` → `processing` → `completed` (usually seconds for the sample file).

On `completed`, progress should look roughly like:

| Field | Approx |
| --- | --- |
| `accepted` | 2 |
| `rejected` | ≥ 2 |
| `processed` | `accepted + rejected` |

(Duplicates are counted inside `rejected`; `duplicates` may be `0` or `>0` depending on timing/batching.)

### 1.4 Summary — `GET /v1/imports/{id}/summary`

**Expect:** `200`, `"isFinal": true`, totals matching status, `byCurrency` including `USD` / `EUR`, `byRiskLevel` present.

### 1.5 Rejections — `GET /v1/imports/{id}/rejections`

1. Call with `limit=1`.
2. **Expect:** one item + `nextCursor`.
3. Call again with that `cursor` and `limit=50`.
4. **Expect:** further items (e.g. `INVALID_AMOUNT`, `INVALID_JSON`); no unbounded dump.

---

## 2. Cancel flow

1. `POST /v1/imports` with a **new** `Idempotency-Key` (e.g. `review-cancel-1`) and the sample file.
2. Immediately `POST /v1/imports/{id}/cancel`.

**Expect:** `200` with `"status":"cancelled"` (if still pending) or `"cancelling"` then shortly `cancelled`.

3. Call cancel again on the same id.

**Expect:** safe repeat (`200` cancelled, or a clear conflict if already terminal in a non-cancellable way).

---

## 3. Validation / error shapes

| Action | Expect |
| --- | --- |
| `POST /v1/imports` without `X-Provider-Id` / `Idempotency-Key` | `400` + `{ "error": { "code", "message", "requestId" } }` |
| `GET /v1/imports/00000000000000000000000000000000` | `404` `IMPORT_NOT_FOUND` |
| `POST /v1/imports/{completedId}/retry` | `409` `IMPORT_NOT_RETRYABLE` |
| Response headers | `X-Request-Id` present (server-generated) |

---

## 4. Retry (optional)

Only `failed` imports can be retried. On a healthy deploy, the sample file completes successfully, so Swagger alone may not produce `failed`.

If you have a failed id (or the candidate seeds one):

1. `POST /v1/imports/{id}/retry` → `200` `"status":"pending"`.
2. Poll until `completed` (or `failed` again with a reason).

---

## 5. Curl equivalents (optional)

```bash
BASE=https://YOUR_HOST
KEY=review-curl-001
PROVIDER=reviewer

# Create
curl -sS -X POST "$BASE/v1/imports" \
  -H "Idempotency-Key: $KEY" \
  -H "X-Provider-Id: $PROVIDER" \
  -F "file=@reviewer-sample.ndjson;type=application/x-ndjson"

# Status / summary / rejections (replace IMPORT_ID)
curl -sS "$BASE/v1/imports/IMPORT_ID"
curl -sS "$BASE/v1/imports/IMPORT_ID/summary"
curl -sS "$BASE/v1/imports/IMPORT_ID/rejections?limit=50"

# Cancel
curl -sS -X POST "$BASE/v1/imports/IMPORT_ID/cancel"
```

---

## Quick checklist

- [ ] Swagger opens
- [ ] `/health/ready` healthy
- [ ] Create import → `202`
- [ ] Idempotent replay → same id
- [ ] Status reaches `completed` with sensible progress
- [ ] Summary is final and grouped
- [ ] Rejections paginate with cursor
- [ ] Cancel works on a new import
- [ ] Errors use stable `error.code` (no stack traces)
- [ ] `/metrics` scrapes

More context: [SUBMISSION.md](SUBMISSION.md) · [ARCHITECTURE.md](ARCHITECTURE.md) · root [README.md](../README.md)
