#!/usr/bin/env bash
# Copy prod compose + env example to the EC2 box (run after terraform apply).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST="${1:?usage: $0 <ec2-user@host> [ssh-key]}"
KEY="${2:-}"
REMOTE_DIR="${DEPLOY_COMPOSE_DIR:-/usr/src/apps/transaction-import}"

SSH=(ssh)
SCP=(scp)
if [[ -n "$KEY" ]]; then
  SSH+=( -i "$KEY" )
  SCP+=( -i "$KEY" )
fi

"${SSH[@]}" "$HOST" "mkdir -p '$REMOTE_DIR'"
"${SCP[@]}" \
  "$ROOT/docker-compose.prod.yml" \
  "$ROOT/.env.example" \
  "$HOST:$REMOTE_DIR/"

"${SSH[@]}" "$HOST" "cd '$REMOTE_DIR' && if [[ ! -f .env ]]; then cp .env.example .env; fi && ls -la"

echo "Synced. On the server (or via CD):"
echo "  cd $REMOTE_DIR"
echo "  docker login ghcr.io   # if packages are private"
echo "  docker compose -f docker-compose.prod.yml pull"
echo "  docker compose -f docker-compose.prod.yml up -d"
