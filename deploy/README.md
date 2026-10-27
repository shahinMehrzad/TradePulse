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

## Things to know

- **Order.** The API creates and migrates the schema, so the panel and the collector wait for it to be healthy; it waits for Postgres.
- **Its own database.** The stack has a named volume (`tradepulse-stack_pgdata`) and does not touch the root `docker-compose.yml`
  or a Postgres you run yourself.
- **HTTP only.** The containers speak plain HTTP on `127.0.0.1`. The panel's cookie is `Secure` outside Development: Chrome and Firefox
  accept it on `http://localhost`, but for any other host put the panel behind a reverse proxy with HTTPS.
- **Secrets** are only in `deploy/.env` (git-ignored). Use long random values; the signing key needs at least 32 characters.
- **Sessions** of the panel are in its memory: restarting the panel signs everyone out.
- **Backups.** The data is in the `pgdata` volume. Liquidations, open interest and the like cannot be downloaded again from the
  exchange, so back it up, e.g. `docker compose -f deploy/docker-compose.yml exec -T postgres pg_dump -U postgres tradepulse | gzip > backup.sql.gz`.
