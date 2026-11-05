#!/usr/bin/env bash
# Replace the placeholder public URL in docs after deploy.
# usage: ./deploy/scripts/update-readme-url.sh https://abzlab-import.duckdns.org
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
URL="${1:?usage: $0 https://your-host.example}"
URL="${URL%/}"

PLACEHOLDER="https://YOUR_PUBLIC_HOST"

files=(
  "$ROOT/README.md"
  "$ROOT/docs/SUBMISSION.md"
  "$ROOT/docs/REVIEWER_TEST_GUIDE.md"
)

for f in "${files[@]}"; do
  if grep -q "$PLACEHOLDER" "$f"; then
    tmp="$(mktemp)"
    sed "s|$PLACEHOLDER|$URL|g" "$f" >"$tmp"
    mv "$tmp" "$f"
    echo "updated $f"
  else
    echo "no placeholder in $f (already set?)"
  fi
done

echo "Public URL is now: $URL"
echo "Swagger: $URL/swagger"
