# Running everything with Docker

Postgres, the API, the admin panel and the collector, in containers of their own. Nothing in the application code is specific to
Docker: the settings come in as environment variables, and everything for the containers lives in this folder.

```bash
cp deploy/.env.example deploy/.env      # then fill it in: passwords, the signing key, the admin
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
```

| What | Where (only on this machine) |
| --- | --- |
| Admin panel | http://localhost:8088 (`PANEL_PORT`) |
| API | http://localhost:8087 (`API_PORT`) |
| Collector `/health` | http://localhost:8089/health (`HEALTH_PORT`) |

Sign in to the panel with `ADMIN_EMAIL` / `ADMIN_PASSWORD`. The collector has nothing to collect until you add a series under
**Collector series**; it applies what you choose within about half a minute. Alerts: [docs/alerts.md](../docs/alerts.md).

```bash
docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs -f collector   # what the collector says
docker compose -f deploy/docker-compose.yml --env-file deploy/.env down                # stop; the data stays
docker compose -f deploy/docker-compose.yml --env-file deploy/.env down -v             # stop AND delete the database
```

## On a server, with a domain (HTTPS)

Passwords, the two-factor codes and the session cookie must never cross the internet in the clear, and the panel's cookie is only sent
over HTTPS, so on a server the panel is reached through Caddy (`deploy/Caddyfile`), which gets and renews the certificate by itself.

1. Buy a domain and add a DNS **A record** for it (e.g. `panel.example.com`) with the server's IP. (A domain also makes the mail the panel
   sends, e.g. sign-in codes, far less likely to land in spam: set SPF and DKIM at your mail provider.)
2. In `deploy/.env` set `DOMAIN` and `ACME_EMAIL`.
3. Open ports **80** and **443** (and nothing else but SSH) in the server's firewall. Port 80 is needed for the certificate request.
4. Start the stack with the `public` profile:

```bash
docker compose -f deploy/docker-compose.yml --env-file deploy/.env --profile public up -d --build
```

Then open `https://<your domain>`. Only the panel is published; the API, Postgres and the collector stay inside the stack or on `127.0.0.1`.

- **Real client addresses.** Caddy passes the visitor's address on; the panel passes it to the API, which counts the login rate limit
  (`RateLimiting:LoginPermitsPerMinute`, per client) by it. The programs believe `X-Forwarded-For` only from a private network, never from the internet.
- **Keys of data protection** live in the volumes `api-keys` and `panel-keys`. Do not delete them with `down -v` unless you want everyone signed out.
- **Even safer.** The panel can place real trades, so consider not exposing it to everyone: keep `--profile public` off and reach
  `127.0.0.1:8088` through a VPN or an SSH tunnel (`ssh -L 8088:127.0.0.1:8088 server`). Then HTTPS and a domain are not needed for the admins.

## Things to know

- **Order.** The API creates and migrates the schema, so the panel and the collector wait for it to be healthy; it waits for Postgres.
- **Its own database.** The stack has a named volume (`tradepulse-stack_pgdata`) and does not touch the root `docker-compose.yml`
  or a Postgres you run yourself. It runs PostgreSQL 18, like the development database, so a `pg_dump -Fc` of that one can be loaded with
  `pg_restore` (a dump from a newer version than the server cannot be read). A stack that was started with the old Postgres 17 definition
  has its data in a volume this definition does not use: dump it first (`pg_dump`), start the new stack and restore.
- **HTTP on this machine only.** The containers speak plain HTTP on `127.0.0.1`. The panel's cookie is `Secure` outside Development: Chrome and Firefox
  accept it on `http://localhost`, but for any other host you need HTTPS: see the next section.
- **The key vault** (`trader`) has no published port and is reached only by the panel. It needs `VAULT_MASTER_KEY`: keep a copy outside the server, see [docs/exchange-keys.md](../docs/exchange-keys.md).
- **Secrets** are only in `deploy/.env` (git-ignored). Use long random values; the signing key needs at least 32 characters.
- **Sessions** of the panel are in its memory: restarting the panel signs everyone out (the cookie keys survive, the sessions do not).
- **Backups.** The data is in the `pgdata` volume. Liquidations, open interest and the like cannot be downloaded again from the
  exchange, so back it up: see "Watching the stack, and backups" below.

## Watching the stack, and backups

The collector sends its own alerts, so it cannot tell you that it is stopped, that Docker is down or that the server is gone. Two
scripts in this folder look at the stack from the outside, and a third makes the backups. They run on the server from cron, as the
user that runs Docker, and read their settings from `deploy/.env` (the names are in `.env.example`).

```bash
crontab -e
# every minute: look at the stack
* * * * * /opt/tradepulse/deploy/watchdog.sh >/dev/null 2>&1
# the 1st and the 15th at 03:00 UTC: a backup
0 3 1,15 * * /opt/tradepulse/deploy/backup.sh >> $HOME/tradepulse-backup.log 2>&1
```
(`chmod +x deploy/*.sh` if the files lost their executable bit.) They write to the system log (`journalctl -t tradepulse-ops`) and,
with `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` set, to the same Telegram chat as the collector. The messages only have names and
numbers in them.

**`watchdog.sh`** checks that every container of the stack is running (and healthy where it has a health check), that the collector
answers on its health port, that Postgres accepts connections, that the disk is below `WATCHDOG_DISK_PERCENT` (85) and that the
newest backup is not older than `BACKUP_MAX_AGE_DAYS` (20; `0` turns this off). A problem is reported after `WATCHDOG_FAILS` runs in a
row (3, so not for a blink), again every `WATCHDOG_REPEAT_MINUTES` (360) while it lasts, and once more when it is over.

**The watcher outside the server.** If the whole server or the cron job dies, nothing on it can say so. So the watchdog also pings
`WATCHDOG_PING_URL` every minute while nothing is broken (and `<url>/fail` while something is), and a service that notices when
the pings STOP tells you. With [healthchecks.io](https://healthchecks.io) (free): create a check with a period of 1 minute and a grace
time of 5 minutes, add Telegram or e-mail as the way to tell you, and put its ping address in `WATCHDOG_PING_URL`. Once the panel has
its domain you can also let an uptime service (for example UptimeRobot) request `https://<domain>/Login` every 5 minutes.

**`backup.sh`** runs `pg_dump -Fc` of the stack's database into `BACKUP_DIR` (`/opt/tradepulse-backups`, outside the git checkout),
checks that the file is readable and not tiny (`BACKUP_MIN_BYTES`, 100000), and keeps the newest `BACKUP_KEEP` (4). **A backup on the
same disk does not survive the disk**: set `BACKUP_RSYNC_TARGET` (`user@host:/path/`, rsync over ssh) or `BACKUP_RCLONE_REMOTE`
(`remote:bucket/folder`) to copy each one off the server; the copies there are not pruned. A failure is reported to Telegram.

Restoring a backup into an empty database (this is how the data was moved to the server):

```bash
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d postgres
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T postgres   pg_restore -U postgres -d tradepulse --no-owner < /opt/tradepulse-backups/tradepulse-YYYYMMDD-HHMMSS.dump
```
To go back over a database that is in use: stop `api`, `panel`, `trader` and `collector` first and add `--clean --if-exists`
(that way is not tried here; try it on a copy first).

The scripts were tried against a throwaway Postgres with fake Telegram, ping and health servers. Not tried: the rsync and rclone
copies, and `flock` (it is not in the shell the tests ran in; Ubuntu has it).
