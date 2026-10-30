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
  or a Postgres you run yourself.
- **HTTP on this machine only.** The containers speak plain HTTP on `127.0.0.1`. The panel's cookie is `Secure` outside Development: Chrome and Firefox
  accept it on `http://localhost`, but for any other host you need HTTPS: see the next section.
- **Secrets** are only in `deploy/.env` (git-ignored). Use long random values; the signing key needs at least 32 characters.
- **Sessions** of the panel are in its memory: restarting the panel signs everyone out (the cookie keys survive, the sessions do not).
- **Backups.** The data is in the `pgdata` volume. Liquidations, open interest and the like cannot be downloaded again from the
  exchange, so back it up, e.g. `docker compose -f deploy/docker-compose.yml exec -T postgres pg_dump -U postgres tradepulse | gzip > backup.sql.gz`.
