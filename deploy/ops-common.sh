# Shared by watchdog.sh and backup.sh: finds the compose file and deploy/.env, reads a setting and sends the Telegram message.
# Source it; do not run it.

# cron starts with a bare PATH.
export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH"

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${ENV_FILE:-$DEPLOY_DIR/.env}"

COMPOSE=(docker compose -f "${COMPOSE_FILE:-$DEPLOY_DIR/docker-compose.yml}" --env-file "$ENV_FILE")
# A project name is only given when asked for (the tests of these scripts); the compose file names the real stack itself.
if [ -n "${COMPOSE_PROJECT:-}" ]; then
  COMPOSE+=(-p "$COMPOSE_PROJECT")
fi

# One setting: the environment first, then deploy/.env, then the default. The value is never printed.
setting() {
  local name="$1" default="${2:-}" value
  value="${!name:-}"
  if [ -z "$value" ] && [ -r "$ENV_FILE" ]; then
    value="$(grep -E "^${name}=" "$ENV_FILE" | tail -n 1 | cut -d= -f2- | tr -d '\r')"
  fi
  printf '%s' "${value:-$default}"
}

# Writes the text to the system log and, when the bot is configured, to the Telegram chat the collector uses.
# Only names and numbers go in the text: never a secret and never an error message of a program.
notify() {
  local text="$1" token chat base
  logger -t tradepulse-ops -- "$text" 2>/dev/null || true

  token="$(setting TELEGRAM_BOT_TOKEN)"
  chat="$(setting TELEGRAM_CHAT_ID)"
  [ -n "$token" ] && [ -n "$chat" ] || return 0
  base="$(setting TELEGRAM_API_BASE https://api.telegram.org)"

  # The token goes in through stdin so that it does not show in the list of processes.
  printf 'url = "%s/bot%s/sendMessage"\n' "$base" "$token" \
    | curl -sS --max-time 15 -o /dev/null -K - \
        --data-urlencode "chat_id=$chat" \
        --data-urlencode "text=[$(setting ALERT_INSTANCE_NAME tradepulse)] $text" \
    || logger -t tradepulse-ops -- "could not send the Telegram message" 2>/dev/null || true
}
