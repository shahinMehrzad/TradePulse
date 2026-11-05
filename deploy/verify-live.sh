#!/usr/bin/env bash
# Read-only look at the three things that unit tests cannot prove, run on the server by hand (never from cron):
#   1. funding rate history: does every funding table hold rows, does it start where it should and does it have holes?
#   2. liquidation stream: did the socket drop and come back (collector log), and is the table still growing?
#   3. shock alerts: has the watch ever stored one (research.shock_alerts), and what did it say last?
# It changes nothing: only SELECTs and `docker compose logs`. It prints names and numbers, never a secret.
#
# Usage: ./deploy/verify-live.sh [days of log to read, default 7]
set -u

. "$(dirname "${BASH_SOURCE[0]}")/ops-common.sh"

DAYS="${1:-7}"
DATABASE="$(setting POSTGRES_DB tradepulse)"
[[ "$DAYS" =~ ^[0-9]{1,3}$ ]] || { echo "days must be a number" >&2; exit 2; }

# sql <statement>: unaligned, tuples only, as the superuser inside the postgres container (like check-db-roles.sh).
# </dev/null: `exec -T` reads stdin, so inside the `while read` loops below it would swallow the rest of the table names.
sql() { "${COMPOSE[@]}" exec -T postgres psql -U postgres -d "$DATABASE" -q -t -A -F ' | ' -c "$1" 2>&1 </dev/null; }

problems=0
flag() { echo "  !! $1"; problems=$((problems + 1)); }

"${COMPOSE[@]}" exec -T postgres pg_isready -U postgres -d "$DATABASE" >/dev/null 2>&1 \
  || { echo "Postgres is not reachable through docker compose; run this on the server, from the repo." >&2; exit 1; }

echo "== 1. Funding rate =="
enabled="$(sql 'SELECT count(*) FROM config.symbol_data_configurations WHERE "CollectFundingRate"')"
earliest="$(sql 'SELECT min("FundingStartDate") FROM config.symbol_data_configurations WHERE "CollectFundingRate"')"
echo "  switched on in the configuration: ${enabled:-?} symbol(s); earliest FundingStartDate: ${earliest:-none}"
tables="$(sql "SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename ~ '^[a-z0-9_]+_fundingrate$' ORDER BY 1")"
if [ -z "$tables" ]; then
  [ "${enabled:-0}" != "0" ] && flag "funding is switched on but there is no funding table yet."
  echo "  no funding table."
else
  count=0
  while read -r t; do
    [[ "$t" =~ ^[a-z0-9_]+$ ]] || continue   # the name is put into SQL below: only what the registry could have made
    count=$((count + 1))
    # rows, oldest, newest and the longest silence between two rows (funding is every 1, 4 or 8 hours: > 9 h is a hole)
    row="$(sql "SELECT count(*), min(funding_time), max(funding_time), coalesce(round(extract(epoch FROM max(gap))/3600), 0)
                FROM (SELECT funding_time, funding_time - lag(funding_time) OVER (ORDER BY funding_time) AS gap FROM public.\"$t\") s")"
    IFS='|' read -r rows oldest newest gaph <<< "$row"
    rows="${rows// /}"; gaph="${gaph// /}"
    printf '  %-48s rows=%s oldest=%s newest=%s longest_gap=%sh\n' "$t" "$rows" "${oldest# }" "${newest# }" "$gaph"
    [ "${rows:-0}" = "0" ] && flag "$t is empty."
    [ "${gaph:-0}" -gt 9 ] 2>/dev/null && flag "$t has a hole of $gaph hours (a paging problem, or a gap Binance itself has)."
  done <<< "$tables"
  [ "${enabled:-0}" != "0" ] && [ "$count" -lt "${enabled:-0}" ] && flag "$count funding table(s) for $enabled switched-on symbol(s)."
fi

echo
echo "== 2. Liquidation stream (collector log, last ${DAYS} d) =="
log="$("${COMPOSE[@]}" logs --no-color --since "${DAYS}d" collector 2>/dev/null)"
lost="$(grep -c "Liquidation stream connection lost" <<< "$log")"
restored="$(grep -c "Liquidation stream connection restored" <<< "$log")"
echo "  connection lost: ${lost}, restored: ${restored}"
if [ "$lost" -eq 0 ]; then
  echo "  no drop seen in this window: the reconnect path is still unproven (not a fault)."
elif [ "$restored" -lt "$lost" ]; then
  flag "the stream was lost $lost time(s) and restored only $restored time(s); check the log around the last 'lost' line."
fi
liq="$(sql "SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename ~ '^[a-z0-9_]+_liquidation$' ORDER BY 1")"
if [ -z "$liq" ]; then
  echo "  no liquidation table."
else
  while read -r t; do
    [[ "$t" =~ ^[a-z0-9_]+$ ]] || continue
    row="$(sql "SELECT count(*), max(event_time), round(extract(epoch FROM (now() at time zone 'utc' - max(event_time)))/3600) FROM public.\"$t\"")"
    IFS='|' read -r rows newest age <<< "$row"
    printf '  %-48s rows=%s newest=%s (%sh ago)\n' "$t" "${rows// /}" "${newest# }" "${age// /}"
  done <<< "$liq"
  echo "  (a quiet symbol may have no liquidation for hours; many tables silent for a day is not normal)"
fi

echo
echo "== 3. Shock alerts =="
if [ "$(sql "SELECT to_regclass('research.shock_alerts') IS NOT NULL")" != "t" ]; then
  flag "research.shock_alerts does not exist (the research migration has not run)."
else
  echo "  stored alerts: $(sql 'SELECT count(*) FROM research.shock_alerts'), in the last 7 days: $(sql "SELECT count(*) FROM research.shock_alerts WHERE \"TriggeredAt\" > now() at time zone 'utc' - interval '7 days'")"
  echo "  newest five (time UTC | listing | direction | level | window s | move | z):"
  sql 'SELECT "TriggeredAt", "SymbolListingId", "Direction", "Level", "WindowSeconds", "Move", "ZScore" FROM research.shock_alerts ORDER BY "TriggeredAt" DESC LIMIT 5' | sed 's/^/    /'
  echo "  (zero alerts after weeks only means the thresholds are strict, not that the watch is broken;"
  echo "   to force one, loosen Watch__Rule__* in deploy/.env for a moment, restart 'watch', then put it back)"
fi

echo
if [ "$problems" -eq 0 ]; then
  echo "Nothing wrong seen."
else
  echo "$problems thing(s) to look at (the lines marked !!)."
fi
exit 0
