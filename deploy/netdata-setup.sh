#!/usr/bin/env bash
# Writes the two settings files of the monitoring agent (Netdata, compose profile `monitoring`) from deploy/.env into deploy/netdata/:
#   go.d/postgres.conf          how it reaches Postgres: as the read-only role tp_monitor (MONITOR_DB_PASSWORD)
#   health_alarm_notify.conf    where its alerts go: the Telegram bot of its own (MONITOR_TELEGRAM_BOT_TOKEN, MONITOR_TELEGRAM_CHAT_ID)
# They are made here, not kept in git, because they hold a password and a bot token. Run it before the first
#   docker compose -f deploy/docker-compose.yml --env-file deploy/.env --profile monitoring up -d
# and again whenever one of those three settings changes (then: ... up -d --force-recreate netdata). The role itself comes from
# deploy/main-db-roles.sh, which makes tp_monitor when MONITOR_DB_PASSWORD is set. See docs/monitoring.md.
set -u -o pipefail

. "$(dirname "${BASH_SOURCE[0]}")/ops-common.sh"

PASSWORD="$(setting MONITOR_DB_PASSWORD)"
DATABASE="$(setting POSTGRES_DB tradepulse)"
TOKEN="$(setting MONITOR_TELEGRAM_BOT_TOKEN)"
CHAT="$(setting MONITOR_TELEGRAM_CHAT_ID)"
OUT="$DEPLOY_DIR/netdata"

if ! [[ "$PASSWORD" =~ ^[A-Za-z0-9]{16,}$ ]]; then
  echo "MONITOR_DB_PASSWORD in $ENV_FILE must be at least 16 letters and digits (openssl rand -hex 24), and the same one that" >&2
  echo "deploy/main-db-roles.sh gave the role tp_monitor. Set it, run ./deploy/main-db-roles.sh, then run this again." >&2
  exit 1
fi
if ! [[ "$DATABASE" =~ ^[A-Za-z0-9_]+$ ]]; then
  echo "POSTGRES_DB must be letters, digits and underscores." >&2
  exit 1
fi

# The two Telegram settings come as a pair, and each has a known shape (nothing else may end up in a config file that a program reads).
alerts="NO"
if [ -n "$TOKEN" ] || [ -n "$CHAT" ]; then
  if ! [[ "$TOKEN" =~ ^[0-9]+:[A-Za-z0-9_-]+$ ]] || ! [[ "$CHAT" =~ ^-?[0-9]+$ ]]; then
    echo "MONITOR_TELEGRAM_BOT_TOKEN and MONITOR_TELEGRAM_CHAT_ID must both be set, the token like 123456:ABC... and the chat id a number" >&2
    echo "(negative for a group). Or leave both empty: then Netdata shows its alarms on the page but tells nobody." >&2
    exit 1
  fi
  alerts="YES"
fi

# The folder is for this user only; the files are readable (the agent inside the container is another user and has to read them).
mkdir -p "$OUT/go.d"
chmod 700 "$OUT"

umask 022
{
  echo "# Written by deploy/netdata-setup.sh. Do not edit and do not commit."
  echo "jobs:"
  echo "  - name: tradepulse"
  # No TLS inside the stack's own network. The role can read the server's statistics and nothing else.
  printf "    dsn: 'postgres://tp_monitor:%s@postgres:5432/%s?sslmode=disable'\n" "$PASSWORD" "$DATABASE"
} > "$OUT/go.d/postgres.conf"

{
  echo "# Written by deploy/netdata-setup.sh. Do not edit and do not commit."
  echo "SEND_EMAIL=\"NO\""
  echo "SEND_TELEGRAM=\"$alerts\""
  if [ "$alerts" = "YES" ]; then
    echo "TELEGRAM_BOT_TOKEN=\"$TOKEN\""
    echo "DEFAULT_RECIPIENT_TELEGRAM=\"$CHAT\""
  fi
} > "$OUT/health_alarm_notify.conf"

echo "Wrote $OUT/go.d/postgres.conf and $OUT/health_alarm_notify.conf."
if [ "$alerts" = "NO" ]; then
  echo "No Telegram bot is set (MONITOR_TELEGRAM_*): the alarms are shown on the Netdata page but nobody is told."
fi
echo "Next: ./deploy/main-db-roles.sh (makes the role tp_monitor), then"
echo "  docker compose -f deploy/docker-compose.yml --env-file deploy/.env --profile monitoring up -d"
echo "and look at it through an ssh tunnel: ssh -L 19999:127.0.0.1:${NETDATA_PORT:-19999} <the server>, then http://localhost:19999"
