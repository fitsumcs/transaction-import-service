#!/usr/bin/env bash
# Repeatable import benchmark: generate NDJSON → upload → poll until terminal → print metrics.
# Prerequisites: API + Worker + Postgres + Redis running (e.g. docker compose up --build).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
API_URL="${API_URL:-http://localhost:8080}"
PROVIDER="${PROVIDER:-bench-provider}"
RECORDS="${RECORDS:-50000}"
WORK_FACTOR_NOTE="${WORK_FACTOR_NOTE:-see Compose RiskScoring__WorkFactor}"
OUT_DIR="${OUT_DIR:-$ROOT/benchmarks/results}"
FILE="$OUT_DIR/bench-${RECORDS}.ndjson"
mkdir -p "$OUT_DIR"

echo "==> Generating $RECORDS records → $FILE"
dotnet run --project "$ROOT/tools/TransactionImport.DataGenerator" -- \
  --records="$RECORDS" --output="$FILE" --duplicates=0.01 --invalid=0.005 --seed=42

KEY="bench-$(date +%s)-$RANDOM"
echo "==> Uploading to $API_URL (Idempotency-Key=$KEY)"
START_UPLOAD=$(date +%s.%N)
CREATE=$(curl -sS -X POST "$API_URL/v1/imports" \
  -H "Idempotency-Key: $KEY" \
  -H "X-Provider-Id: $PROVIDER" \
  -F "file=@${FILE};type=application/x-ndjson")
END_UPLOAD=$(date +%s.%N)
IMPORT_ID=$(python3 -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$CREATE")
echo "    import id: $IMPORT_ID"
echo "    upload wall seconds: $(python3 -c "print(round(float('$END_UPLOAD')-float('$START_UPLOAD'), 3))")"

START_PROC=$(date +%s.%N)
STATUS=""
while true; do
  BODY=$(curl -sS "$API_URL/v1/imports/$IMPORT_ID")
  STATUS=$(python3 -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$BODY")
  if [[ "$STATUS" == "completed" || "$STATUS" == "failed" || "$STATUS" == "cancelled" ]]; then
    break
  fi
  # Sample API latency while import runs
  curl -sS -o /dev/null -w "api_status_ms=%{time_total}\n" "$API_URL/v1/imports/$IMPORT_ID" >>"$OUT_DIR/api-latency-${IMPORT_ID}.txt" || true
  sleep 0.5
done
END_PROC=$(date +%s.%N)

PROC_SECS=$(python3 -c "print(round(float('$END_PROC')-float('$START_PROC'), 3))")
python3 - <<PY
import json, pathlib
body = json.loads('''$BODY''')
progress = body["progress"]
proc = float("$PROC_SECS")
records = int("$RECORDS")
rps = round(progress["processed"] / proc, 1) if proc > 0 else 0
report = {
  "importId": "$IMPORT_ID",
  "status": body["status"],
  "recordsRequested": records,
  "processed": progress["processed"],
  "accepted": progress["accepted"],
  "rejected": progress["rejected"],
  "duplicates": progress["duplicates"],
  "processingSeconds": proc,
  "recordsPerSecond": rps,
  "workFactorNote": "$WORK_FACTOR_NOTE",
  "apiUrl": "$API_URL",
}
path = pathlib.Path("$OUT_DIR") / f"result-{records}.json"
path.write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps(report, indent=2))
print(f"Wrote {path}")
PY

# Optional: capture process RSS if running on host pid (best-effort)
if command -v docker >/dev/null 2>&1; then
  docker stats --no-stream --format 'table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}' 2>/dev/null | tee "$OUT_DIR/docker-stats-${IMPORT_ID}.txt" || true
fi
