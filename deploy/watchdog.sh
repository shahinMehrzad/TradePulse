#!/usr/bin/env bash
# The watchman of the stack, run every minute from cron (see deploy/README.md).
#
# The collector sends its own alerts, so it cannot tell when it is stopped, when Docker is down or when the whole server is gone.
# This script looks from the outside of it, on this machine:
#   - every container of the stack is running and, where it has a health check, healthy;
#   - the collector answers on its health port;
#   - Postgres accepts connections;
#   - the disk is not (nearly) full;
#   - the newest backup is not too old.
# A problem is reported to the Telegram chat after WATCHDOG_FAILS runs in a row (not for a blink), again every
# WATCHDOG_REPEAT_MINUTES while it lasts, and once more when it is over.
#
# And it pings WATCHDOG_PING_URL every run while nothing is broken (and <url>/fail while something is): a service such as
# healthchecks.io then reports to you when the pings STOP, which is how you hear about a dead server or a dead cron job.
set -u

. "$(dirname "${BASH_SOURCE[0]}")/ops-common.sh"

STATE_DIR="$(setting WATCHDOG_STATE_DIR /var/tmp/tradepulse-watchdog)"
THRESHOLD="$(setting WATCHDOG_FAILS 3)"
REPEAT_MINUTES="$(setting WATCHDOG_REPEAT_MINUTES 360)"
SERVICES="$(setting WATCHDOG_SERVICES 'postgres api trader panel collector')"
DISK_LIMIT="$(setting WATCHDOG_DISK_PERCENT 85)"
HEALTH_PORT="$(setting HEALTH_PORT 8089)"
BACKUP_DIR="$(setting BACKUP_DIR /opt/tradepulse-backups)"
BACKUP_MAX_AGE_DAYS="$(setting BACKUP_MAX_AGE_DAYS 20)"
PING_URL="$(setting WATCHDOG_PING_URL)"

mkdir -p "$STATE_DIR"

# One run at a time (a slow Docker answer must not pile runs up).
if command -v flock >/dev/null 2>&1; then
  exec 9>"$STATE_DIR/lock"
  flock -n 9 || exit 0
fi

now="$(date +%s)"
broken=0

# record <what> <status 0=fine> <what is wrong>: keeps how long a check has been failing and talks when it should.
record() {
  local key="$1" status="$2" detail="$3" file="$STATE_DIR/$1" fails=0 alerted=0 last=0

  if [ -r "$file" ]; then
    read -r fails alerted last < "$file" || true
  fi

  if [ "$status" -eq 0 ]; then
    if [ "$alerted" = 1 ]; then
      notify "RECOVERED: $key is fine again."
    fi
    rm -f "$file"
    return 0
  fi

  fails=$((fails + 1))
  if [ "$alerted" != 1 ] && [ "$fails" -ge "$THRESHOLD" ]; then
    notify "PROBLEM: $detail"
    alerted=1
    last="$now"
  elif [ "$alerted" = 1 ] && [ $((now - last)) -ge $((REPEAT_MINUTES * 60)) ]; then
    notify "STILL BROKEN: $detail"
    last="$now"
  fi

  if [ "$alerted" = 1 ]; then
    broken=$((broken + 1))
  fi
  printf '%s %s %s\n' "$fails" "$alerted" "$last" > "$file"
  return 1
}

# ---- the containers
if ps_out="$("${COMPOSE[@]}" ps --all --format '{{.Service}} {{.State}} {{.Health}}' 2>/dev/null)"; then
  record docker 0 ""
  for service in $SERVICES; do
    line="$(printf '%s\n' "$ps_out" | awk -v s="$service" '$1 == s { print; exit }')"
    state="$(printf '%s' "$line" | awk '{ print $2 }')"
    health="$(printf '%s' "$line" | awk '{ print $3 }')"
    state="${state:-missing}"

    if [ "$state" != "running" ]; then
      record "container-$service" 1 "the $service container is $state (it should be running)."
    elif [ "$health" = "unhealthy" ]; then
      record "container-$service" 1 "the $service container is unhealthy."
    else
      record "container-$service" 0 ""
    fi
  done
else
  record docker 1 "Docker does not answer (is it running?)."
fi

# ---- the collector, on its own health port
case " $SERVICES " in
  *" collector "*)
    if curl -fsS --max-time 5 -o /dev/null "http://127.0.0.1:$HEALTH_PORT/health/live" 2>/dev/null; then
      record collector-live 0 ""
    else
      record collector-live 1 "the collector does not answer on its health port ($HEALTH_PORT)."
    fi
    ;;
esac

# ---- Postgres
case " $SERVICES " in
  *" postgres "*)
    if "${COMPOSE[@]}" exec -T postgres pg_isready -U postgres -q >/dev/null 2>&1; then
      record postgres-ready 0 ""
    else
      record postgres-ready 1 "Postgres does not accept connections."
    fi
    ;;
esac

# ---- the disk
used="$(df --output=pcent / 2>/dev/null | tail -n 1 | tr -dc '0-9')"
if [ -n "$used" ] && [ "$used" -ge "$DISK_LIMIT" ]; then
  record disk 1 "the disk is $used% full (the limit is $DISK_LIMIT%)."
else
  record disk 0 ""
fi

# ---- the backups (0 days turns this check off)
if [ "$BACKUP_MAX_AGE_DAYS" -gt 0 ]; then
  newest="$(ls -1t "$BACKUP_DIR"/tradepulse-*.dump 2>/dev/null | head -n 1)"
  if [ -z "$newest" ]; then
    record backup 1 "no backup has been made yet (looked in $BACKUP_DIR)."
  else
    age_days=$(( (now - $(stat -c %Y "$newest")) / 86400 ))
    if [ "$age_days" -gt "$BACKUP_MAX_AGE_DAYS" ]; then
      record backup 1 "the newest backup is $age_days days old (the limit is $BACKUP_MAX_AGE_DAYS)."
    else
      record backup 0 ""
    fi
  fi
fi

# ---- tell the outside that this watchman is alive
if [ -n "$PING_URL" ]; then
  if [ "$broken" -eq 0 ]; then
    curl -fsS --max-time 10 -o /dev/null "$PING_URL" 2>/dev/null || true
  else
    curl -fsS --max-time 10 -o /dev/null "${PING_URL%/}/fail" 2>/dev/null || true
  fi
fi

exit 0
