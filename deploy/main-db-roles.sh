#!/usr/bin/env bash
# Makes (or updates) the roles of the programs in the MAIN database: tp_api, tp_collector, tp_trader and tp_watch, with exactly the rights that
# deploy/main-db-roles.sql gives them. The passwords are API_DB_PASSWORD, COLLECTOR_DB_PASSWORD, TRADER_MAIN_DB_PASSWORD and WATCH_DB_PASSWORD from
# deploy/.env (the programs connect with the same ones). With MONITOR_DB_PASSWORD set too, the read-only role tp_monitor is made for the
# monitoring agent (docs/monitoring.md); without it there is no such role. The same goes for SIGNALS_DB_PASSWORD (role tp_signals, the service that
# takes signals) and SIGNAL_SOURCE_DB_PASSWORD (role tp_signal_source, the program that makes them, read-only on the data tables).
#
# Run it as the user that runs docker compose:
#   - on a new server: before the API starts, and once more after it has started (its migrations make the tables the last grants need);
#   - on an older server: once before the new version is started (the tables change hands), and once more after the API has started;
#   - again after a restore of the main database from a backup, and whenever a password changes.
# It can be run any number of times, and while the stack is running. See docs/database-security.md.
set -u -o pipefail

. "$(dirname "${BASH_SOURCE[0]}")/ops-common.sh"

API_PASSWORD="$(setting API_DB_PASSWORD)"
COLLECTOR_PASSWORD="$(setting COLLECTOR_DB_PASSWORD)"
TRADER_PASSWORD="$(setting TRADER_MAIN_DB_PASSWORD)"
WATCH_PASSWORD="$(setting WATCH_DB_PASSWORD)"
MONITOR_PASSWORD="$(setting MONITOR_DB_PASSWORD)"
# The two roles of the signals are optional like the monitor: made only when their passwords are given.
SIGNALS_PASSWORD="$(setting SIGNALS_DB_PASSWORD)"
SIGNAL_SOURCE_PASSWORD="$(setting SIGNAL_SOURCE_DB_PASSWORD)"
DATABASE="$(setting POSTGRES_DB tradepulse)"

for name in API_DB_PASSWORD COLLECTOR_DB_PASSWORD TRADER_MAIN_DB_PASSWORD WATCH_DB_PASSWORD; do
  value="$(setting "$name")"
  if ! [[ "$value" =~ ^[A-Za-z0-9]{16,}$ ]]; then
    echo "$name in $ENV_FILE must be at least 16 letters and digits (openssl rand -hex 24)." >&2
    exit 1
  fi
done

# The passwords of the monitor and of the two signal roles are optional; when one is given it has the same rules.
for name in MONITOR_DB_PASSWORD SIGNALS_DB_PASSWORD SIGNAL_SOURCE_DB_PASSWORD; do
  value="$(setting "$name")"
  if [ -n "$value" ] && ! [[ "$value" =~ ^[A-Za-z0-9]{16,}$ ]]; then
    echo "$name in $ENV_FILE must be at least 16 letters and digits (openssl rand -hex 24), or empty." >&2
    exit 1
  fi
done

if [ "$(printf '%s\n' "$API_PASSWORD" "$COLLECTOR_PASSWORD" "$TRADER_PASSWORD" "$WATCH_PASSWORD" ${MONITOR_PASSWORD:+"$MONITOR_PASSWORD"} ${SIGNALS_PASSWORD:+"$SIGNALS_PASSWORD"} ${SIGNAL_SOURCE_PASSWORD:+"$SIGNAL_SOURCE_PASSWORD"} | sort | uniq -d)" ]; then
  echo "The passwords (API_DB_PASSWORD, COLLECTOR_DB_PASSWORD, TRADER_MAIN_DB_PASSWORD, WATCH_DB_PASSWORD, MONITOR_DB_PASSWORD, SIGNALS_DB_PASSWORD, SIGNAL_SOURCE_DB_PASSWORD) must be different from each other." >&2
  exit 1
fi

# The passwords go in through stdin, so that they show in no list of processes: first the psql variables, then the SQL.
{
  printf "\\\\set api_password '%s'\n" "$API_PASSWORD"
  printf "\\\\set collector_password '%s'\n" "$COLLECTOR_PASSWORD"
  printf "\\\\set trader_password '%s'\n" "$TRADER_PASSWORD"
  printf "\\\\set watch_password '%s'\n" "$WATCH_PASSWORD"
  printf "\\\\set monitor_password '%s'\n" "$MONITOR_PASSWORD"
  printf "\\\\set signals_password '%s'\n" "$SIGNALS_PASSWORD"
  printf "\\\\set signal_source_password '%s'\n" "$SIGNAL_SOURCE_PASSWORD"
  cat "$DEPLOY_DIR/main-db-roles.sql"
} | "${COMPOSE[@]}" exec -T postgres psql -U postgres -d "$DATABASE" -v ON_ERROR_STOP=1 -q \
  || { echo "Could not set up the roles." >&2; exit 1; }

echo "The roles tp_api, tp_collector, tp_trader and tp_watch are ready in database $DATABASE."
[ -n "$MONITOR_PASSWORD" ] && echo "The read-only role tp_monitor is ready too (monitoring)."
[ -n "$SIGNALS_PASSWORD" ] && echo "The role tp_signals is ready too (the service that takes signals)."
[ -n "$SIGNAL_SOURCE_PASSWORD" ] && echo "The read-only role tp_signal_source is ready too (the program that makes signals)."
echo "If a notice above says a table does not exist yet, run this script again after the API has started."
