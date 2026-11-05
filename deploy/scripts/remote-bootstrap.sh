#!/usr/bin/env bash
# Copy bootstrap-vps.sh to a password-based VM and run it over SSH.
#
# usage:
#   ./deploy/scripts/remote-bootstrap.sh user@VM_IP abzlab-import.duckdns.org you@example.com
#
# You will be prompted for the SSH password (unless you already use a key).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TARGET="${1:?usage: $0 user@host domain email [--skip-cert]}"
DOMAIN="${2:?}"
EMAIL="${3:?}"
EXTRA=("${@:4}")

SCRIPT_LOCAL="$ROOT/deploy/scripts/bootstrap-vps.sh"
REMOTE_TMP="/tmp/bootstrap-vps.sh"

scp "$SCRIPT_LOCAL" "$TARGET:$REMOTE_TMP"
ssh -t "$TARGET" "sudo bash $REMOTE_TMP --domain '$DOMAIN' --email '$EMAIL' ${EXTRA[*]-}"
