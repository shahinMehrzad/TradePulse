#!/usr/bin/env bash
# Checks the database roles on the running stack: each role connects with its own password over the network (so a wrong password really is
# refused), does what its program needs, and is refused what it must not do. Nothing is changed: every write is rolled back.
#
#   ./deploy/check-db-roles.sh
#
# It prints "ok" or "FAIL" for each check and exits with 1 if any failed. Run it after main-db-roles.sh and after every deploy that
# touches the database. See docs/database-security.md.
set -u -o pipefail

. "$(dirname "${BASH_SOURCE[0]}")/ops-common.sh"

DATABASE="$(setting POSTGRES_DB tradepulse)"
api_pw="$(setting API_DB_PASSWORD)"
collector_pw="$(setting COLLECTOR_DB_PASSWORD)"
trader_pw="$(setting TRADER_MAIN_DB_PASSWORD)"
watch_pw="$(setting WATCH_DB_PASSWORD)"
# Only when monitoring is used (docs/monitoring.md): without this password there is no tp_monitor to check.
monitor_pw="$(setting MONITOR_DB_PASSWORD)"
failed=0

# run <role> <password> <sql>: the output and the exit code of psql, connecting to the server by its network name (the image trusts
# connections from inside its own container on localhost, so that way a wrong password would pass).
run() {
  PGPASSWORD="$2" "${COMPOSE[@]}" exec -T -e PGPASSWORD postgres \
    psql -h postgres -U "$1" -d "$DATABASE" -v ON_ERROR_STOP=1 -q -t -A -c "$3" 2>&1
}

ok() { echo "ok    $1"; }
fail() { echo "FAIL  $1${2:+ ($2)}"; failed=1; }

# can <role> <password> <what it is> <sql>
can() {
  local out
  if out="$(run "$1" "$2" "$4")"; then ok "$1 can $3"; else fail "$1 can $3" "$(echo "$out" | head -n 1)"; fi
}

# cannot <role> <password> <what it is> <sql>: it must be refused for lack of rights, not for another reason
cannot() {
  local out
  if out="$(run "$1" "$2" "$4")"; then
    fail "$1 must not be able to $3" "it was allowed"
  elif grep -q -E "permission denied|must be owner" <<<"$out"; then
    ok "$1 cannot $3"
  else
    fail "$1 cannot $3" "refused, but not for lack of rights: $(echo "$out" | head -n 1)"
  fi
}

superuser() { "${COMPOSE[@]}" exec -T postgres psql -U postgres -d "$DATABASE" -q -t -A -c "$1" 2>&1; }

echo "Database: $DATABASE"

# ---- the passwords work, and a wrong one does not
for pair in "tp_api:$api_pw" "tp_collector:$collector_pw" "tp_trader:$trader_pw" "tp_watch:$watch_pw"; do
  role="${pair%%:*}"; pw="${pair#*:}"
  if [ -z "$pw" ]; then fail "$role has a password in deploy/.env"; continue; fi
  if run "$role" "$pw" "select 1" >/dev/null; then ok "$role connects with its password"; else fail "$role connects with its password" "run deploy/main-db-roles.sh"; fi
  if run "$role" "not-the-password-$RANDOM" "select 1" >/dev/null; then fail "$role refuses a wrong password"; else ok "$role refuses a wrong password"; fi
done

# One data table to try the data rights on (the collector made it). None yet: those checks are skipped.
DATA_TABLE="$(superuser "select tablename from pg_tables where schemaname = 'public' order by tablename limit 1")"

# ---- tp_api
can    tp_api "$api_pw" "read the accounts"                     "select count(*) from auth.users"
can    tp_api "$api_pw" "read the configuration"                "select count(*) from config.candle_worker_configurations"
can    tp_api "$api_pw" "read the audit log"                    "select count(*) from config.audit_log"
can    tp_api "$api_pw" "read the research tables"              "select count(*) from research.market_events"
can    tp_api "$api_pw" "make a table in its own schema"        "begin; create table config.check_tmp (x int); rollback"
cannot tp_api "$api_pw" "make a table in public"                "create table public.check_tmp (x int)"
if [ -n "$DATA_TABLE" ]; then
  can    tp_api "$api_pw" "read the data table $DATA_TABLE"     "select count(*) from public.\"$DATA_TABLE\""
  cannot tp_api "$api_pw" "delete from the data table"          "begin; delete from public.\"$DATA_TABLE\"; rollback"
  cannot tp_api "$api_pw" "drop the data table"                 "drop table public.\"$DATA_TABLE\""
else
  echo "skip  the data table checks of tp_api (no table in public yet)"
fi

# ---- tp_collector
can    tp_collector "$collector_pw" "read what to collect (series)"       "select count(*) from config.candle_worker_configurations"
can    tp_collector "$collector_pw" "read what to collect (symbol data)"  "select count(*) from config.symbol_data_configurations"
can    tp_collector "$collector_pw" "read the symbol listings"            "select count(*) from config.exchange_symbols"
can    tp_collector "$collector_pw" "make a data table"                   "begin; create table public.check_tmp (x int); rollback"
cannot tp_collector "$collector_pw" "read the accounts"                   "select count(*) from auth.users"
cannot tp_collector "$collector_pw" "read the second-factor secrets"      "select count(*) from auth.user_tokens"
cannot tp_collector "$collector_pw" "read the counts of wrong passwords"  "select count(*) from auth.login_throttles"
cannot tp_collector "$collector_pw" "read the audit log"                  "select count(*) from config.audit_log"
cannot tp_collector "$collector_pw" "read the research tables"            "select count(*) from research.market_events"
cannot tp_collector "$collector_pw" "change the configuration"            "begin; delete from config.candle_worker_configurations; rollback"
cannot tp_collector "$collector_pw" "make a table in config"              "create table config.check_tmp (x int)"
cannot tp_collector "$collector_pw" "make a schema"                       "create schema check_tmp"
if [ -n "$DATA_TABLE" ]; then
  can  tp_collector "$collector_pw" "write the data table $DATA_TABLE"    "begin; delete from public.\"$DATA_TABLE\" where false; rollback"
fi

# ---- tp_trader
can    tp_trader "$trader_pw" "add to the audit log"      "begin; insert into config.audit_log (\"OccurredAt\", \"UserName\", \"Action\", \"EntityType\", \"EntityId\", \"Summary\") values (now(), 'check', 'Added', 'Check', 0, 'check'); rollback"
can    tp_trader "$trader_pw" "publish the symbols"       "begin; insert into config.traded_symbols values ('CHECKUSDT', now()); delete from config.traded_symbols; rollback"
cannot tp_trader "$trader_pw" "read the audit log"        "select count(*) from config.audit_log"
cannot tp_trader "$trader_pw" "read the symbols"          "select count(*) from config.traded_symbols"
cannot tp_trader "$trader_pw" "read the accounts"         "select count(*) from auth.users"
cannot tp_trader "$trader_pw" "read the configuration"    "select count(*) from config.candle_worker_configurations"
[ -n "$DATA_TABLE" ] && cannot tp_trader "$trader_pw" "read the data table" "select count(*) from public.\"$DATA_TABLE\""

# ---- tp_watch
can    tp_watch "$watch_pw" "read the watch list"            "select count(*) from config.shock_watch_configurations"
can    tp_watch "$watch_pw" "read the symbol listings"       "select count(*) from config.exchange_symbols join config.symbols on true join config.exchanges on true join config.markets on true"
can    tp_watch "$watch_pw" "read the enabled symbols"       "select count(*) from config.traded_symbols"
can    tp_watch "$watch_pw" "write its alerts"               "begin; update research.shock_alerts set \"Move\" = \"Move\" where false; rollback"
cannot tp_watch "$watch_pw" "delete its alerts"            "begin; delete from research.shock_alerts; rollback"
cannot tp_watch "$watch_pw" "read the accounts"              "select count(*) from auth.users"
cannot tp_watch "$watch_pw" "read the second-factor secrets" "select count(*) from auth.user_tokens"
cannot tp_watch "$watch_pw" "read the audit log"             "select count(*) from config.audit_log"
cannot tp_watch "$watch_pw" "change the watch list"          "begin; delete from config.shock_watch_configurations; rollback"
cannot tp_watch "$watch_pw" "change the configuration"       "begin; delete from config.candle_worker_configurations; rollback"
cannot tp_watch "$watch_pw" "make a table in public"         "create table public.check_tmp (x int)"
cannot tp_watch "$watch_pw" "make a table in research"       "create table research.check_tmp (x int)"
if [ -n "$DATA_TABLE" ]; then
  can    tp_watch "$watch_pw" "read the data table $DATA_TABLE"    "select count(*) from public.\"$DATA_TABLE\""
  cannot tp_watch "$watch_pw" "write the data table"               "begin; delete from public.\"$DATA_TABLE\"; rollback"
fi

# ---- tp_monitor (only when monitoring is set up): the statistics of the server, and no row of anything
if [ -n "$monitor_pw" ]; then
  if run tp_monitor "$monitor_pw" "select 1" >/dev/null; then ok "tp_monitor connects with its password"; else fail "tp_monitor connects with its password" "run deploy/main-db-roles.sh"; fi
  if run tp_monitor "not-the-password-$RANDOM" "select 1" >/dev/null; then fail "tp_monitor refuses a wrong password"; else ok "tp_monitor refuses a wrong password"; fi
  can    tp_monitor "$monitor_pw" "read the statistics of the server"  "select count(*) from pg_stat_activity"
  can    tp_monitor "$monitor_pw" "read the size of the database"      "select pg_database_size(current_database())"
  cannot tp_monitor "$monitor_pw" "read the accounts"                  "select count(*) from auth.users"
  cannot tp_monitor "$monitor_pw" "read the second-factor secrets"     "select count(*) from auth.user_tokens"
  cannot tp_monitor "$monitor_pw" "read the audit log"                 "select count(*) from config.audit_log"
  cannot tp_monitor "$monitor_pw" "read the configuration"             "select count(*) from config.candle_worker_configurations"
  cannot tp_monitor "$monitor_pw" "change the configuration"           "begin; delete from config.candle_worker_configurations; rollback"
  cannot tp_monitor "$monitor_pw" "make a table in public"             "create table public.check_tmp (x int)"
  if [ -n "$DATA_TABLE" ]; then
    cannot tp_monitor "$monitor_pw" "read the data table $DATA_TABLE"  "select count(*) from public.\"$DATA_TABLE\""
  fi
fi

# ---- who is connected right now (the running programs should be on their own roles)
echo
echo "Connections to $DATABASE now, by role:"
superuser "select coalesce(usename, '?'), count(*) from pg_stat_activity where datname = '$DATABASE' and backend_type = 'client backend' group by 1 order by 1" \
  | sed 's/|/  /; s/^/  /'
others="$(superuser "select count(*) from pg_stat_activity where datname = '$DATABASE' and backend_type = 'client backend' and usename = 'postgres' and application_name not like 'psql%' and application_name not like 'pg\_%'")"
if [ "${others:-0}" -gt 0 ]; then
  fail "no program connects as the superuser" "$others connection(s) as postgres that are not psql or pg_dump: a program still uses the old connection string (restart it with the current docker-compose.yml)"
else
  ok "no program connects as the superuser"
fi

echo
if [ "$failed" -eq 0 ]; then echo "All checks passed."; else echo "Some checks FAILED."; fi
exit "$failed"
