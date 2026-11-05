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
SERVICES="$(setting WATCHDOG_SERVICES 'postgres vault-db api trader panel collector watch')"
DISK_LIMIT="$(setting WATCHDOG_DISK_PERCENT 85)"
HEALTH_PORT="$(setting HEALTH_PORT 8089)"
BACKUP_DIR="$(setting BACKUP_DIR /opt/tradepulse-backups)"
BACKUP_MAX_AGE_DAYS="$(setting BACKUP_MAX_AGE_DAYS 20)"
PING_URL="$(setting WATCHDOG_PING_URL)"
TELEGRAM_CHECK_MINUTES="$(setting WATCHDOG_TELEGRAM_CHECK_MINUTES 60)"
TELEGRAM_FAILS="$(setting WATCHDOG_TELEGRAM_FAILS 2)"

mkdir -p "$STATE_DIR"

# One run at a time (a slow Docker answer must not pile runs up).
if command -v flock >/dev/null 2>&1; then
  exec 9>"$STATE_DIR/lock"
  flock -n 9 || exit 0
fi

now="$(date +%s)"
broken=0

# record <what> <status 0=fine> <what is wrong> [runs in a row before it talks]: keeps how long a check has been failing and talks when it should.
record() {
  local key="$1" status="$2" detail="$3" file="$STATE_DIR/$1" fails=0 alerted=0 last=0 threshold="${4:-$THRESHOLD}"

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
  if [ "$alerted" != 1 ] && [ "$fails" -ge "$threshold" ]; then
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

# ---- the key vault's own Postgres
case " $SERVICES " in
  *" vault-db "*)
    if "${COMPOSE[@]}" exec -T vault-db pg_isready -U vault -q >/dev/null 2>&1; then
      record vault-db-ready 0 ""
    else
      record vault-db-ready 1 "The key vault's Postgres does not accept connections."
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
  newest="$(ls -1t "$BACKUP_DIR"/tradepulse-*.dump "$BACKUP_DIR"/tradepulse-*.dump.age 2>/dev/null | head -n 1)"
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

# ---- the backup of the vault's database (once one exists: the stack may not have been upgraded to its own database yet)
if [ "$BACKUP_MAX_AGE_DAYS" -gt 0 ]; then
  newest_vault="$(ls -1t "$BACKUP_DIR"/vaultdb-*.dump "$BACKUP_DIR"/vaultdb-*.dump.age 2>/dev/null | head -n 1)"
  if [ -n "$newest_vault" ]; then
    age_days=$(( (now - $(stat -c %Y "$newest_vault")) / 86400 ))
    if [ "$age_days" -gt "$BACKUP_MAX_AGE_DAYS" ]; then
      record vault-backup 1 "the newest backup of the vault's database is $age_days days old (the limit is $BACKUP_MAX_AGE_DAYS)."
    else
      record vault-backup 0 ""
    fi
  fi
fi

# ---- the Telegram chats of the bots (collector, price watch, security alerts, monitoring)
# A bot whose chat has gone (a group upgraded to a supergroup gets a new id, the bot was removed or blocked) cannot say so itself, and its
# program looks healthy. Once an hour each configured bot is asked "may you still post there?" with sendChatAction ("typing..." for a few
# seconds, nothing stays in the chat). A bot that may not is reported through a bot that still can, and the ping below turns to /fail, which
# healthchecks.io carries to you by its own channels. Telegram being unreachable proves nothing about a chat: it changes no state.
telegram_probe() {
  local token="$1" chat="$2" base answer reason
  base="$(setting TELEGRAM_API_BASE https://api.telegram.org)"
  # The token goes in through stdin so that it does not show in the list of processes.
  answer="$(printf 'url = "%s/bot%s/sendChatAction"\n' "$base" "$token" \
    | curl -sS --max-time 15 -K - --data-urlencode "chat_id=$chat" --data-urlencode "action=typing" 2>/dev/null)" || return 2
  [ -n "$answer" ] || return 2
  if printf '%s' "$answer" | grep -q '"ok" *: *true'; then
    return 0
  fi
  # Telegram's own sentence about the request ("Bad Request: group chat was upgraded to a supergroup chat"); the token is blanked should it hold it.
  reason="$(printf '%s' "$answer" | sed -n 's/.*"description" *: *"\([^"]*\)".*/\1/p' | head -c 160)"
  printf '%s' "${reason//$token/***}"
  return 1
}

if [ "$TELEGRAM_CHECK_MINUTES" -gt 0 ] 2>/dev/null; then
  channels="collector:TELEGRAM_BOT_TOKEN:TELEGRAM_CHAT_ID watch:WATCH_TELEGRAM_BOT_TOKEN:WATCH_TELEGRAM_CHAT_ID security:SECURITY_TELEGRAM_BOT_TOKEN:SECURITY_TELEGRAM_CHAT_ID monitor:MONITOR_TELEGRAM_BOT_TOKEN:MONITOR_TELEGRAM_CHAT_ID"
  stamp="$STATE_DIR/telegram-checked"
  last_check=0
  [ -r "$stamp" ] && read -r last_check < "$stamp"
  [[ "$last_check" =~ ^[0-9]+$ ]] || last_check=0

  if [ $((now - last_check)) -ge $((TELEGRAM_CHECK_MINUTES * 60)) ]; then
    printf '%s\n' "$now" > "$stamp"
    declare -A verdict reason tokens chats
    via_token=""
    via_chat=""

    for entry in $channels; do
      IFS=: read -r name token_setting chat_setting <<< "$entry"
      tokens[$name]="$(setting "$token_setting")"
      chats[$name]="$(setting "$chat_setting")"
      if [ -z "${tokens[$name]}" ] || [ -z "${chats[$name]}" ]; then
        verdict[$name]=unset
        continue
      fi
      reason[$name]="$(telegram_probe "${tokens[$name]}" "${chats[$name]}")"
      case $? in
        0) verdict[$name]=ok
           if [ -z "$via_token" ]; then via_token="${tokens[$name]}"; via_chat="${chats[$name]}"; fi ;;
        1) verdict[$name]=bad ;;
        *) verdict[$name]=unknown ;;
      esac
    done

    for entry in $channels; do
      name="${entry%%:*}"
      case "${verdict[$name]}" in
        ok|unset)
          NOTIFY_VIA_TOKEN="$via_token" NOTIFY_VIA_CHAT="$via_chat" record "telegram-$name" 0 "" ;;
        bad)
          detail="the $name bot can no longer post in its Telegram chat (Telegram says: ${reason[$name]:-no reason given}). If the group was upgraded to a supergroup its chat id changed: put the new one in deploy/.env."
          NOTIFY_VIA_TOKEN="$via_token" NOTIFY_VIA_CHAT="$via_chat" record "telegram-$name" 1 "$detail" "$TELEGRAM_FAILS" ;;
        *)
          # Telegram did not answer: nothing is learned, so what was known before still stands (and still counts as broken).
          if [ -r "$STATE_DIR/telegram-$name" ]; then
            read -r _ was_alerted _ < "$STATE_DIR/telegram-$name" || true
            [ "${was_alerted:-0}" = 1 ] && broken=$((broken + 1))
          fi ;;
      esac
    done
  fi

  # A run that does not look must still count what is known to be wrong, or the ping would say "all well" in between.
  if [ "${looked:-0}" -ne 1 ]; then
    for entry in $channels; do
      file="$STATE_DIR/telegram-${entry%%:*}"
      if [ -r "$file" ]; then
        read -r _ was_alerted _ < "$file" || true
        [ "${was_alerted:-0}" = 1 ] && broken=$((broken + 1))
      fi
    done
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
