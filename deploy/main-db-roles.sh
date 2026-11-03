#!/usr/bin/env bash
# Makes (or updates) the one role the key vault ("trader") has in the MAIN database: tp_trader. It may add rows to the audit log and
# publish the copy of the enabled symbols, and nothing else: it cannot read a single table. The password is TRADER_MAIN_DB_PASSWORD
# from deploy/.env (the same one the trader connects with).
#
# Run it as the user that runs docker compose, once the API has started (its migrations make the two tables), and again whenever the
# password changes or after the main database is restored from a backup (a restore with --no-owner leaves the grants out).
# It can be run any number of times. See docs/database-security.md.
set -u -o pipefail

. "$(dirname "${BASH_SOURCE[0]}")/ops-common.sh"

PASSWORD="$(setting TRADER_MAIN_DB_PASSWORD)"
DATABASE="$(setting POSTGRES_DB tradepulse)"

if ! [[ "$PASSWORD" =~ ^[A-Za-z0-9]{16,}$ ]]; then
  echo "TRADER_MAIN_DB_PASSWORD in $ENV_FILE must be at least 16 letters and digits (openssl rand -hex 24)." >&2
  exit 1
fi

# The password goes in through stdin, so that it shows in no list of processes. The first line sets it as a psql variable.
{
  printf "\\\\set password '%s'\n" "$PASSWORD"
  cat <<'SQL'
-- Create the role when it is not there, then (re)set its password. format(%L) quotes it.
SELECT format('CREATE ROLE tp_trader LOGIN PASSWORD %L', :'password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'tp_trader') \gexec
SELECT format('ALTER ROLE tp_trader LOGIN PASSWORD %L', :'password') \gexec

-- Nothing beyond the two tables below: a plain role can neither read nor change anything else, and cannot make tables.
SELECT format('GRANT CONNECT ON DATABASE %I TO tp_trader', current_database()) \gexec
GRANT USAGE ON SCHEMA config TO tp_trader;
GRANT INSERT ON config.audit_log TO tp_trader;
GRANT INSERT, DELETE ON config.traded_symbols TO tp_trader;
SQL
} | "${COMPOSE[@]}" exec -T postgres psql -U postgres -d "$DATABASE" -v ON_ERROR_STOP=1 -q \
  || { echo "Could not set up the role. Is the API up (it makes config.audit_log and config.traded_symbols)?" >&2; exit 1; }

echo "Role tp_trader is ready in database $DATABASE."
