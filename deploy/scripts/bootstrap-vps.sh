#!/usr/bin/env bash
# Bootstrap a bare Ubuntu VPS for transaction-import (Yegara / any password-SSH VM).
#
# Run ON the server as root (or a sudo user), after you can SSH in:
#   curl -fsSL ... | sudo bash   # or copy this file up and:
#   sudo bash bootstrap-vps.sh --domain abzlab-import.duckdns.org --email you@example.com
#
# What it does:
#   - installs Docker + Compose plugin, Nginx, Certbot
#   - creates /usr/src/apps/transaction-import
#   - configures Nginx reverse-proxy → 127.0.0.1:8080
#   - optionally issues Let's Encrypt cert (needs DNS already pointing here)
#
# It does NOT pull GHCR images or start the app (do that after copying compose/.env).
set -euo pipefail

DOMAIN=""
EMAIL=""
APP_DIR="/usr/src/apps/transaction-import"
APP_USER="${SUDO_USER:-${USER:-root}}"
SKIP_CERT=0

usage() {
  cat <<'EOF'
Usage: sudo bash bootstrap-vps.sh --domain HOST [--email YOU@example.com] [--app-dir DIR] [--skip-cert]

Examples:
  sudo bash bootstrap-vps.sh --domain abzlab-import.duckdns.org --email you@example.com
  sudo bash bootstrap-vps.sh --domain abzlab-import.duckdns.org --skip-cert
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --domain) DOMAIN="${2:?}"; shift 2 ;;
    --email) EMAIL="${2:?}"; shift 2 ;;
    --app-dir) APP_DIR="${2:?}"; shift 2 ;;
    --skip-cert) SKIP_CERT=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; usage; exit 1 ;;
  esac
done

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Run as root: sudo bash $0 ..." >&2
  exit 1
fi

if [[ -z "$DOMAIN" ]]; then
  echo "--domain is required" >&2
  usage
  exit 1
fi

if [[ "$SKIP_CERT" -eq 0 && -z "$EMAIL" ]]; then
  echo "--email is required unless --skip-cert" >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive

echo "==> Packages"
apt-get update -y
apt-get install -y ca-certificates curl gnupg nginx certbot python3-certbot-nginx

if ! command -v docker >/dev/null 2>&1; then
  echo "==> Docker"
  curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker

# Compose plugin comes with get.docker.com; ensure present
docker compose version >/dev/null

if id "$APP_USER" >/dev/null 2>&1 && [[ "$APP_USER" != "root" ]]; then
  usermod -aG docker "$APP_USER" || true
fi

echo "==> App dir $APP_DIR"
mkdir -p "$APP_DIR"
if id "$APP_USER" >/dev/null 2>&1; then
  chown -R "$APP_USER:$APP_USER" "$APP_DIR"
fi

echo "==> Nginx → 127.0.0.1:8080"
cat >/etc/nginx/sites-available/transaction-import <<EOF
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name ${DOMAIN};

    client_max_body_size 512m;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 300s;
        proxy_send_timeout 300s;
    }
}
EOF

ln -sf /etc/nginx/sites-available/transaction-import /etc/nginx/sites-enabled/transaction-import
rm -f /etc/nginx/sites-enabled/default
nginx -t
systemctl enable --now nginx
systemctl reload nginx

if [[ "$SKIP_CERT" -eq 0 ]]; then
  echo "==> Let's Encrypt for ${DOMAIN}"
  echo "    (DNS A record must already point to this VM's public IP)"
  certbot --nginx -d "$DOMAIN" --non-interactive --agree-tos -m "$EMAIL" --redirect
else
  echo "==> Skipping certbot (--skip-cert). Run later:"
  echo "    sudo certbot --nginx -d ${DOMAIN} -m ${EMAIL:--m YOU@example.com} --agree-tos --redirect"
fi

PUBLIC_IP="$(curl -fsS --max-time 5 https://ifconfig.me 2>/dev/null || curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || echo unknown)"

cat <<EOF

==> Bootstrap complete

App dir:     ${APP_DIR}
Domain:      ${DOMAIN}
Public IP:   ${PUBLIC_IP}   ← set DuckDNS A record to this if not already

Next on this VM (or via scp from your laptop):
  1. Copy docker-compose.prod.yml + .env into ${APP_DIR}
  2. docker login ghcr.io   # if packages are private
  3. cd ${APP_DIR} && docker compose -f docker-compose.prod.yml pull && docker compose -f docker-compose.prod.yml up -d
  4. curl -fsS http://127.0.0.1:8080/health/ready
  5. Open https://${DOMAIN}/swagger

From your Mac (password SSH):
  scp docker-compose.prod.yml .env.example ${APP_USER}@${PUBLIC_IP}:${APP_DIR}/
EOF
