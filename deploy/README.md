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
  The keys in `api-keys` also encrypt every user's authenticator key in the database (the recovery codes are stored as hashes). Without
  those keys nobody can sign in with their authenticator app. Each user then needs a two-factor reset by another administrator, or
  `reset-access` for the last one. Keep a copy of the `api-keys` volume next to the database backups, but not in the same place: a
  database dump is only useless to a thief while it is stored apart from these keys.
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
  It has **its own Postgres** (`vault-db`, volume `vault-pgdata`) on a network nothing else is on.
- **Database roles.** No program connects as the superuser: the API is `tp_api`, the collector `tp_collector`, the trader's way into the main
  database `tp_trader`, the price watch `tp_watch` (passwords `API_DB_PASSWORD`, `COLLECTOR_DB_PASSWORD`, `TRADER_MAIN_DB_PASSWORD`, `WATCH_DB_PASSWORD`, and
  `VAULT_DB_PASSWORD` for the vault's Postgres). `./deploy/main-db-roles.sh` makes the roles and gives them their rights (run it **before** the API starts the first time and again
  **after** it, and after any restore); `./deploy/check-db-roles.sh` tries each role and says `ok` or `FAIL`. Setting up a new server,
  **upgrading a running one**, restoring, looking at the databases and defining a role yourself: [docs/database-security.md](../docs/database-security.md).
- **Secrets** are only in `deploy/.env` (git-ignored). Use long random values. The tokens are signed with a key pair (`./deploy/make-jwt-keys.sh` prints
  `JWT_PRIVATE_KEY` and `JWT_PUBLIC_KEY`): the private half only in the API's environment, the public half in the key vault's, which therefore cannot
  make a token. A new pair signs everybody out of the panel once; the sessions come back by themselves from their refresh tokens, and the
  10-minute confirmation of a second-factor code has to be given again.
- **Sessions** of the panel are in its memory: restarting the panel signs everyone out (the cookie keys survive, the sessions do not).
- **Backups.** The data is in the `pgdata` volume (and the key vault's in `vault-pgdata`; `backup.sh` dumps both). Liquidations, open interest and the like cannot be downloaded again from the
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

It also looks at the **Telegram chats** of the four bots (collector, price watch, security alerts, monitoring) once an hour (`WATCHDOG_TELEGRAM_CHECK_MINUTES`,
60; `0` turns it off): each configured bot is asked, with `sendChatAction` ("typing..." for a few seconds, nothing stays in the chat), whether it
may still post in its chat. A bot cannot report that its own chat is gone, and its program looks healthy; this happened when a group was
upgraded to a supergroup and so got a new chat id. After `WATCHDOG_TELEGRAM_FAILS` checks in a row (2) the problem is reported through a bot that
still works, with Telegram's own reason, and the ping turns to `/fail` so that healthchecks.io tells you by its own channels, which do not depend
on any of the bots. If Telegram itself cannot be reached nothing is concluded and nothing changes. Tried against a small fake Telegram
(a moved chat of each of the bots, the recovery, an unreachable Telegram); not against the real one.

**The watcher outside the server.** If the whole server or the cron job dies, nothing on it can say so. So the watchdog also pings
`WATCHDOG_PING_URL` every minute while nothing is broken (and `<url>/fail` while something is), and a service that notices when
the pings STOP tells you. With [healthchecks.io](https://healthchecks.io) (free): create a check with a period of 1 minute and a grace
time of 5 minutes, add Telegram or e-mail as the way to tell you, and put its ping address in `WATCHDOG_PING_URL`. Once the panel has
its domain you can also let an uptime service (for example UptimeRobot) request `https://<domain>/Login` every 5 minutes.

**`backup.sh`** runs `pg_dump -Fc` of the stack's databases (the main one, and the key vault's when `vault-db` runs) into `BACKUP_DIR` (`/opt/tradepulse-backups`, outside the git checkout),
checks that the file is readable and not tiny (`BACKUP_MIN_BYTES`, 100000), and keeps the newest `BACKUP_KEEP` (4) of each kind. **A backup on the
same disk does not survive the disk**: set `BACKUP_RSYNC_TARGET` (`user@host:/path/`, rsync over ssh) or `BACKUP_RCLONE_REMOTE`
(`remote:bucket/folder`) to copy each one off the server; the copies there are not pruned. A failure is reported to Telegram.

**Encrypt the backups** (`BACKUP_AGE_RECIPIENT`, an [age](https://github.com/FiloSottile/age) public key): a dump holds every account, the
audit log and everything else in the database, and it is copied to places that are less guarded than this server. With the key set, each file
is encrypted before it is kept or copied (it then ends in `.age`), the plain dump exists only in memory while the script works, and the keys of
data protection (which decrypt the authenticator keys) are saved in the same way as `api-keys-*.tgz.age`, so nothing has to be done by hand
for them. Only the owner of the private key, which is not on the server, can read a backup. Set-up, the test to do once, and restoring:
[docs/database-security.md](../docs/database-security.md#encrypted-backups).

**A copy on your own computer: `pull-backups.ps1`.** Run on a Windows machine (PowerShell 5.1 or 7, OpenSSH client), it lists the `.age`
files in the server's backup folder over ssh, downloads the ones it does not have (a `.part` file first, checked for size and for the age header,
then renamed) and deletes the older local copies so that `-Keep` (8) of each kind stay. Nothing is deleted on the server. Nothing about the
server or the folder is written in the script; each person who runs it gives their own:

```powershell
.\deploy\pull-backups.ps1 -Server <user>@<your-server>                         # into <your profile>\tradepulse-backups
.\deploy\pull-backups.ps1 -Server <user>@<your-server> -Destination <folder> -Keep 12 -Register -At 04:00   # and run it every day
```

The same can be set with the environment variables `TRADEPULSE_SERVER`, `TRADEPULSE_BACKUP_DIR` and `TRADEPULSE_REMOTE_BACKUP_DIR`.
`-Register` makes a daily Windows task for the current user (it runs once now, too, unless `-NoRun`), so the ssh key of that user must work
without a passphrase prompt. Run it more often than the server's cron job makes backups and than `BACKUP_KEEP` can hide: the server keeps only the
newest four. Plain `.dump` files are refused unless `-AllowPlain`. The files stay encrypted here; decrypt one with your age private key only
when you restore. Tried: the parsing, the checks and the tidying of the local folder. Not tried: the ssh/scp part against a real server.

**`verify-live.sh`** (run by hand on the server, never from cron; only reads) shows what the unit tests cannot: the funding rate tables
(rows, oldest and newest settlement, the longest hole), the liquidation stream (lost/restored lines in the collector log, newest event) and the
shock alerts the watch stored. `./deploy/verify-live.sh 7` reads the last 7 days of the log.

Restoring a backup into an empty database (this is how the data was moved to the server). After a restore of the main database run
`./deploy/main-db-roles.sh` again (a restore with `--no-owner` makes `postgres` the owner of everything and drops the grants); the vault's dump (`vaultdb-*.dump`) is restored into `vault-db`, see
[docs/database-security.md](../docs/database-security.md):

```bash
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d postgres
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T postgres   pg_restore -U postgres -d tradepulse --no-owner < /opt/tradepulse-backups/tradepulse-YYYYMMDD-HHMMSS.dump
```
(A file that ends in `.age` has to be decrypted first, with the private key, on your computer: see "Encrypted backups" in the page above.)
To go back over a database that is in use: stop `api`, `panel`, `trader` and `collector` first and add `--clean --if-exists`
(that way is not tried here; try it on a copy first).

The scripts were tried against a throwaway Postgres with fake Telegram, ping and health servers. Not tried: the rsync and rclone
copies, and `flock` (it is not in the shell the tests ran in; Ubuntu has it).
