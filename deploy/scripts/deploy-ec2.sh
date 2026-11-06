#!/usr/bin/env bash
# End-to-end deploy onto EC2 (or any Ubuntu host):
#   A) Terraform path: create EC2 + cloud-init (Docker/Nginx) then sync compose + up
#   B) Existing host:  --host ubuntu@IP  (manual EC2 / Yegara) → bootstrap + sync + up
#
# usage (from repo root):
#   # Existing EC2 you created in the console (recommended if Terraform SCP blocks EC2):
#   ./deploy/scripts/deploy-ec2.sh --host ubuntu@1.2.3.4 --key ~/.ssh/id_ed25519 \
#     --domain abzlab-import.duckdns.org --email you@example.com
#
#   # Terraform (needs EC2 API allowed on the account):
#   export AWS_PROFILE=abzlab
#   ./deploy/scripts/deploy-ec2.sh
#
#   GHCR_USER=fitsumcs GHCR_TOKEN=ghp_... ./deploy/scripts/deploy-ec2.sh --host ...
#
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TF_DIR="$ROOT/deploy/terraform"
APP_DIR="${DEPLOY_COMPOSE_DIR:-/usr/src/apps/transaction-import}"

TARGET=""
SSH_KEY="${HOME}/.ssh/id_ed25519"
DOMAIN=""
EMAIL=""
SKIP_APPLY=0
SKIP_UP=0
SKIP_BOOTSTRAP=0
UPDATE_README=0
PUBLIC_BASE_URL=""

usage() {
  cat <<'EOF'
Usage:
  ./deploy/scripts/deploy-ec2.sh --host user@IP --key ~/.ssh/KEY --domain HOST --email YOU@x.com
  ./deploy/scripts/deploy-ec2.sh [--skip-apply] [--skip-up] [--update-readme]

Options:
  --host user@ip       Use an existing VM (skip Terraform). Ubuntu AMI user is usually "ubuntu".
  --key PATH           SSH private key (default ~/.ssh/id_ed25519)
  --domain HOST        Public hostname for Nginx/TLS (e.g. abzlab-import.duckdns.org)
  --email EMAIL        Let's Encrypt email (required with --domain unless --skip-bootstrap)
  --skip-bootstrap     Do not run bootstrap-vps.sh (Docker/Nginx already installed)
  --skip-apply         Terraform mode only: do not terraform apply
  --skip-up            Sync compose/.env only; do not docker compose up
  --update-readme      Rewrite docs YOUR_PUBLIC_HOST after deploy
  -h, --help

Env:
  AWS_PROFILE / AWS credentials   required for Terraform mode
  GHCR_USER + GHCR_TOKEN          optional docker login on the instance for private GHCR
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --host) TARGET="${2:?}"; shift 2 ;;
    --key) SSH_KEY="${2:?}"; shift 2 ;;
    --domain) DOMAIN="${2:?}"; shift 2 ;;
    --email) EMAIL="${2:?}"; shift 2 ;;
    --skip-bootstrap) SKIP_BOOTSTRAP=1; shift ;;
    --skip-apply) SKIP_APPLY=1; shift ;;
    --skip-up) SKIP_UP=1; shift ;;
    --update-readme) UPDATE_README=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; usage; exit 1 ;;
  esac
done

need() { command -v "$1" >/dev/null 2>&1 || { echo "Missing dependency: $1" >&2; exit 1; }; }
need ssh
need scp

SSH_KEY="${SSH_KEY/#\~/$HOME}"

# ---------- resolve target: existing host OR terraform ----------
if [[ -n "$TARGET" ]]; then
  if [[ ! -f "$SSH_KEY" ]]; then
    echo "SSH key not found: $SSH_KEY" >&2
    exit 1
  fi
  IP="${TARGET##*@}"
  PUBLIC_BASE_URL="${DOMAIN:+https://$DOMAIN}"
  PUBLIC_BASE_URL="${PUBLIC_BASE_URL:-http://$IP}"
else
  need terraform
  need aws
  if [[ ! -f "$TF_DIR/terraform.tfvars" ]]; then
    echo "Missing $TF_DIR/terraform.tfvars (or pass --host for a manual EC2)." >&2
    exit 1
  fi
  TF_KEY="$(
    awk -F'=|#' '/^[[:space:]]*ssh_private_key_path[[:space:]]*=/{
      gsub(/[[:space:]]|"/, "", $2); print $2; exit
    }' "$TF_DIR/terraform.tfvars"
  )"
  TF_KEY="${TF_KEY/#\~/$HOME}"
  if [[ -n "$TF_KEY" && -f "$TF_KEY" ]]; then
    SSH_KEY="$TF_KEY"
  fi
  if [[ ! -f "$SSH_KEY" ]]; then
    echo "SSH private key not found." >&2
    exit 1
  fi

  echo "==> AWS identity"
  aws sts get-caller-identity

  cd "$TF_DIR"
  [[ -d .terraform ]] || terraform init
  if [[ "$SKIP_APPLY" -eq 0 ]]; then
    echo "==> terraform apply"
    terraform apply -auto-approve
  else
    echo "==> skipping terraform apply"
  fi
  IP="$(terraform output -raw public_ip)"
  PUBLIC_BASE_URL="$(terraform output -raw public_base_url)"
  TARGET="ubuntu@${IP}"
  DOMAIN="${DOMAIN:-$(awk -F'=|#' '/^[[:space:]]*domain_name[[:space:]]*=/{gsub(/[[:space:]]|"/, "", $2); print $2; exit}' "$TF_DIR/terraform.tfvars" || true)}"
  SKIP_BOOTSTRAP=1   # cloud-init already installed Docker/Nginx
fi

SSH=(ssh -i "$SSH_KEY" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10)
SCP=(scp -i "$SSH_KEY" -o StrictHostKeyChecking=accept-new)

echo "==> waiting for SSH on ${TARGET}"
for i in $(seq 1 60); do
  if "${SSH[@]}" "$TARGET" "echo ok" >/dev/null 2>&1; then
    echo "    SSH ready (attempt $i)"
    break
  fi
  if [[ "$i" -eq 60 ]]; then
    echo "Timed out waiting for SSH: ${SSH[*]} $TARGET" >&2
    exit 1
  fi
  sleep 5
done

# ---------- bootstrap Docker/Nginx on bare hosts ----------
if [[ "$SKIP_BOOTSTRAP" -eq 0 ]]; then
  if [[ -z "$DOMAIN" || -z "$EMAIL" ]]; then
    echo "--domain and --email are required when bootstrapping a bare host" >&2
    exit 1
  fi
  echo "==> bootstrap Docker + Nginx + TLS on ${TARGET}"
  # Point DuckDNS yourself first (or ignore if A record already set).
  "${SCP[@]}" "$ROOT/deploy/scripts/bootstrap-vps.sh" "$TARGET:/tmp/bootstrap-vps.sh"
  "${SSH[@]}" -t "$TARGET" "sudo bash /tmp/bootstrap-vps.sh --domain '$DOMAIN' --email '$EMAIL'"
else
  echo "==> waiting for Docker (cloud-init / previous bootstrap)"
  for i in $(seq 1 60); do
    if "${SSH[@]}" "$TARGET" "command -v docker >/dev/null && docker compose version >/dev/null"; then
      echo "    Docker ready"
      break
    fi
    if [[ "$i" -eq 60 ]]; then
      echo "Docker not ready. Re-run with bootstrap (omit --skip-bootstrap) or wait for cloud-init." >&2
      exit 1
    fi
    sleep 5
  done
fi

echo "==> sync compose to ${APP_DIR}"
"${SSH[@]}" "$TARGET" "sudo mkdir -p '$APP_DIR' && sudo chown -R \$(id -u):\$(id -g) '$APP_DIR' 2>/dev/null || mkdir -p '$APP_DIR'"
"${SCP[@]}" \
  "$ROOT/docker-compose.prod.yml" \
  "$ROOT/.env.example" \
  "$TARGET:$APP_DIR/"
"${SSH[@]}" "$TARGET" "cd '$APP_DIR' && if [[ ! -f .env ]]; then cp .env.example .env; fi && ls -la"

if [[ "$SKIP_UP" -eq 1 ]]; then
  echo "==> skip compose up (--skip-up)"
else
  if [[ -n "${GHCR_TOKEN:-}" && -n "${GHCR_USER:-}" ]]; then
    echo "==> docker login ghcr.io on instance"
    "${SSH[@]}" "$TARGET" "echo '$GHCR_TOKEN' | docker login ghcr.io -u '$GHCR_USER' --password-stdin"
  else
    echo "==> no GHCR_USER/GHCR_TOKEN — assuming public images or already logged in"
  fi

  echo "==> docker compose pull && up -d"
  "${SSH[@]}" "$TARGET" "cd '$APP_DIR' && docker compose -f docker-compose.prod.yml pull && docker compose -f docker-compose.prod.yml up -d --remove-orphans && docker compose -f docker-compose.prod.yml ps"

  echo "==> health check"
  for i in $(seq 1 30); do
    if "${SSH[@]}" "$TARGET" "curl -fsS http://127.0.0.1:8080/health/ready" >/dev/null 2>&1; then
      echo "    API ready"
      break
    fi
    if [[ "$i" -eq 30 ]]; then
      echo "API not ready. Logs: ${SSH[*]} $TARGET 'cd $APP_DIR && docker compose -f docker-compose.prod.yml logs --tail=100'" >&2
      exit 1
    fi
    sleep 3
  done
fi

SWAGGER_URL="${PUBLIC_BASE_URL%/}/swagger"
if [[ "$UPDATE_README" -eq 1 ]]; then
  echo "==> update README / docs → ${PUBLIC_BASE_URL}"
  "$ROOT/deploy/scripts/update-readme-url.sh" "$PUBLIC_BASE_URL"
fi

cat <<EOF

==> Deploy finished

Host:        ${TARGET}
Base URL:    ${PUBLIC_BASE_URL}
Swagger:     ${SWAGGER_URL}
SSH:         ssh -i ${SSH_KEY} ${TARGET}
Compose dir: ${APP_DIR}

GitHub CD secrets:
  DEPLOY_HOST=${IP}
  DEPLOY_USER=${TARGET%%@*}
  DEPLOY_PORT=22
  DEPLOY_COMPOSE_DIR=${APP_DIR}
  DEPLOY_SSH_KEY=<contents of ${SSH_KEY}>

Before reviewers hit HTTPS: DuckDNS A record must point to ${IP}.
EOF
