# Watching the server

The stack can run a monitoring agent next to the programs: [Netdata](https://www.netdata.cloud), in the compose profile `monitoring`. It is
optional and off by default. It shows, with history, what a server's owner is asked about most: CPU, memory, disk, the memory of **every
container over days** (a leak is a slope that only drops when the container is restarted), the connections to Postgres and what the database
does. And it tells a Telegram chat when something crosses its limit.

It does not replace `deploy/watchdog.sh` and the dead man's switch ([deploy/README.md](../deploy/README.md)): those look from the outside and
say "the server or a program is gone"; Netdata lives on the server and says "it is getting full" long before.

## What it measures

| Where in its page | What |
|---|---|
| System overview | CPU, load, memory, swap, disk I/O, network of its container, pressure (how long things waited for CPU, memory or disk) |
| Disks | Space used on `/` (the disk of the stack) and the time left until it is full at the present rate |
| Containers | CPU and **memory of each container by its name** (`deploy-api-1`, `deploy-collector-1`, ...), the thing to look at for a leak |
| PostgreSQL | Connections used against `max_connections`, by state, per database; transactions, cache hits, I/O, locks, size |

Its stock alarms are on, among them: memory in use, CPU over ten minutes, load, swap, **disk space and the time until it is full**, the
out-of-memory killer, and the connections of Postgres against its limit. Nothing was changed in them.

**Not measured, on purpose:** the key vault's own Postgres (`vault-db`) is on a network that nothing else may join, so the agent does not get
a way in; its container (CPU, memory) is measured like the others. The network of the host is not seen either (the agent does not share it);
neither are single processes (it does not share the host's process space).

## Setting it up

1. In `deploy/.env` (the template has the lines): `MONITOR_DB_PASSWORD` (letters and digits, 16 or more, `openssl rand -hex 24`, different from
   every other password) and, for the alerts, `MONITOR_TELEGRAM_BOT_TOKEN` and `MONITOR_TELEGRAM_CHAT_ID`: a bot of its own (BotFather), whose
   chat you have started, in a group of its own if you like (`/start@bot` shows the id, negative for a group). Without the two, the alarms are
   only on the page.
2. Make the database role, which can read the server's statistics and **no row of anything**:
   ```bash
   ./deploy/main-db-roles.sh
   ```
3. Write the agent's two settings files from `deploy/.env` (they hold the password and the token, so they are made here and are not in git):
   ```bash
   ./deploy/netdata-setup.sh
   ```
4. Start it. The first time this downloads about 1.3 GB (the Netdata image) and keeps its history in two volumes:
   ```bash
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env --profile monitoring up -d
   ```
5. Check the role: `./deploy/check-db-roles.sh` now also checks `tp_monitor`. And check that the alerts arrive, with a test message to the chat:
   ```bash
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec netdata /usr/libexec/netdata/plugins.d/alarm-notify.sh test
   ```
   It sends a warning, a critical and a clear message. "wrong bot token" in its answer means the token is wrong; "chat not found", the chat id.
6. Look at it. The page is bound to the server's own address (`127.0.0.1:19999`), never published. From your computer:
   ```bash
   ssh -L 19999:127.0.0.1:19999 <user>@<the server>
   ```
   and open <http://localhost:19999>. (When there is a VPN, the page can be reached through it instead.)

If a setting changes later (the password or the bot), run `./deploy/netdata-setup.sh` again and `... up -d --force-recreate netdata`.

To have the watchdog also notice when the agent itself is down, add it to `WATCHDOG_SERVICES` in `deploy/.env`
(`postgres vault-db api trader panel collector watch netdata`). It is not in the default list because the agent is optional.

## Looking for a memory leak

Open **Containers & VMs**, choose a container and a window of several days, and look at *memory usage*: a healthy .NET program rises after a
start and then stays about level (the garbage collector keeps a ceiling); a leak is a line that keeps climbing and only falls when the
container restarts. Compare it with the number of connections to Postgres on the PostgreSQL page. Nothing here sees inside the program (the
size of the .NET heap, the number of threads): that needs the OpenTelemetry metrics of the roadmap.

## What keeps it harmless

This server holds the key vault, so the agent is given as little as it can work with:

- It runs as an ordinary user with **no capabilities**, `no-new-privileges`, and in its own process and network space (not `pid: host`, not
  `network_mode: host`, not privileged). What it reads of the host (`/proc`, `/sys`, `os-release`) is mounted read-only.
- It does not get the Docker socket, which would be the key to the whole host. A small proxy (`docker-proxy`) holds the socket and answers
  one thing, "list the containers", so that the agent can put names to the numbers; anything else (creating, starting, images, info) is refused
  with `403`. They share a network (`monitor-net`) that nothing else is on and that has no way out.
- Postgres sees it as `tp_monitor`: it connects, may read the statistics views (`pg_monitor`), cannot read a table (not the accounts, the
  audit log, the configuration or a candle), change anything or make anything. The tests of `DatabaseRolesTests` and `check-db-roles.sh` say so.
- Anonymous statistics are off (`DO_NOT_TRACK`) and the agent is not claimed to Netdata Cloud (no claim token in the stack): nothing leaves the
  server except the Telegram messages.
- The image and the proxy are pinned to a version in `deploy/docker-compose.yml`; raise them on purpose, not by pulling `latest`.

## Tried

On a throwaway copy of the stack (a Postgres, the agent and the proxy): the role is made and refused everything but statistics; the agent
reads its two files, connects to Postgres, shows the containers by name, loads its alarms, runs without capabilities, and the proxy answers the
list and refuses everything else; the alert path reads its settings and tries the chat (with a made-up token it answers "wrong bot token").
**Not tried:** a real bot token, and a Linux server of the kind that runs the stack (the rehearsal ran in Docker Desktop's virtual machine, whose
kernel and cgroups differ a little: look at the page once on the server and compare the disk and the container names).
