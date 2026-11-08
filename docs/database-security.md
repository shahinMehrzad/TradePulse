# Databases and who may touch what

The stack has **two Postgres servers**, and **every program that uses a database has a role of its own** with only the rights it needs.
No program connects as the superuser any more. This page says what the layout is and why, how to set up a server or upgrade a running one,
how to look at and check the databases, and how to define a role yourself.

## What it protects against

| If this happens | What the attacker gets | What stays out of reach |
| --- | --- | --- |
| A copy of the **main** database leaks (a dump, a backup) | Accounts, candles, configuration, the audit log. The second-factor secrets in it are encrypted or hashed ([deploy/README.md](../deploy/README.md)). | The encrypted private keys, the orders, the daily budget, the halt switch: they are not in that database. |
| The **collector** is hacked (it talks to the internet all day) | The candle tables (it writes them anyway) and the four config tables that say what to collect. | The accounts, the second-factor secrets, the audit log, the research tables. It cannot change the configuration or make a schema. The vault: no network path and no password. |
| The **API** is hacked | Its three schemas (accounts, configuration, research) and a read of the data tables. | It cannot write or delete a candle. The vault: no network path and no password. |
| The **panel** is hacked | Nothing in a database: it has none. | |
| The **vault's database** leaks | The private keys, still encrypted with `VAULT_MASTER_KEY`, which is only in the trader's environment. Orders and limits. | Nothing is usable without the master key. |
| The **trader** is hacked | The vault's database, the master key, and a role in the main database that can only add audit rows and publish the symbol list. | It cannot read the main database (not even the audit log it writes to). |

## The layout

```
                  internet
                     |
                   Caddy            (80/443, the only published ports)
                     |
                   panel ----------------------------+
                     |                               |
        network: default                             |
   +-----------------+------------------+            |
   |                 |                  |            |
  api            collector            postgres     trader ---- Binance (internet, over "default")
 (tp_api)        (tp_collector)      (main DB)       |
   |                 |                  ^            |
   +------ postgres -+                  |      network: vault-net (internal: no internet,
                                        |        only the trader and vault-db are on it)
                            tp_trader --+            |
                     (add audit rows,                |
                      publish symbols)            vault-db   (the vault's own Postgres)
```

| Program | Database | Role | May |
| --- | --- | --- | --- |
| API | main (`postgres` service, database `tradepulse`) | `tp_api` | Owns the schemas `auth`, `config`, `research`, `signals`: its migrations create and change their tables. `SELECT` on the data tables in `public`. `CREATE` on the database (only so `CREATE SCHEMA IF NOT EXISTS` in its migrations is allowed). |
| collector | main | `tp_collector` | Owns the data tables in `public`, and may create new ones there. `SELECT` on `config.candle_worker_configurations`, `config.symbol_data_configurations`, `config.exchange_symbols`, `config.symbols`. Nothing else. |
| trader | vault (`vault-db` service, database `vault`) | `vault` | Everything in its own database. It is the only one that has the password, and nothing else is stored there. |
| trader | main | `tp_trader` | `INSERT` on `config.audit_log`; `INSERT`, `DELETE` on `config.traded_symbols`; `USAGE` on schema `config`. It cannot `SELECT` anything, `UPDATE`, or create tables. |
| price watch | main | `tp_watch` | `SELECT` on `config.shock_watch_configurations`, `config.exchange_symbols`, `config.symbols`, `config.exchanges`, `config.markets`, `config.traded_symbols` and on the data tables in `public`; `SELECT`, `INSERT`, `UPDATE` on `research.shock_alerts`. It cannot see `auth` or the audit log, change the watch list or the configuration, or create tables. |
| monitoring agent (optional) | main | `tp_monitor` | `pg_monitor`: the statistics views and settings of the server (who is connected, reads, writes, sizes). No table can be read, nothing changed or made. Made only when `MONITOR_DB_PASSWORD` is set. See [monitoring.md](monitoring.md). |
| signal service (optional) | main | `tp_signals` | `SELECT` on `config.exchange_symbols`, `config.symbols`, `config.exchanges`, `config.markets`, `config.signal_strategy_configurations` (the strategies it takes signals from, and their limits) and on the data tables in `public`; `SELECT`, `INSERT` on `signals.signals` and `signals.events`; `SELECT`, `INSERT`, `UPDATE` on `signals.paper_trades`; `USAGE` on schemas `config` and `signals`. It deletes nothing, cannot see `auth`, the audit log or `research`, cannot change the configuration or create tables. Made only when `SIGNALS_DB_PASSWORD` is set. |
| signal source (optional) | main | `tp_signal_source` | `SELECT` on the data tables in `public` and nothing else: no configuration, no signal, no trade. It is the private program that makes the signals; it sends them over the webhook and has no way into the signals' tables. Made only when `SIGNAL_SOURCE_DB_PASSWORD` is set. |
| panel | none | none | The panel has no database. It asks the API and the trader. |
| backups, this script | main | `postgres` (superuser) | Used inside the postgres container only (`pg_dump`, `psql` by `docker exec`). No program has its password. |

Why the rights are what they are (so you can judge a change):

- **The collector owns the data tables** because it creates them (`CREATE TABLE IF NOT EXISTS`) and for the liquidation tables an index
  (`CREATE INDEX IF NOT EXISTS`). Postgres demands ownership of the table for the index statement even when the index exists, so a
  role that only has `INSERT` would fail there. The API gets `SELECT` on them, including on tables made later (`ALTER DEFAULT PRIVILEGES`).
- **The API owns `auth`, `config`, `research`** because EF Core migrations alter their tables. Postgres also checks the right to create
  schemas for `CREATE SCHEMA IF NOT EXISTS` even when the schema exists, hence `CREATE` on the database. That right lets a role make
  schemas and nothing else.
- **The collector reads four config tables** and not the rest: they are what its two providers read (`candle_worker_configurations`,
  `symbol_data_configurations` with the listing and symbol they point to). The tests run the collector's own code under this role, so a
  table it needs and lacks fails a test, not production.

What lives where:

- **Vault database** (schema `vault`): `exchange_keys` (public key, encrypted private key, API key), `orders`, `trading_state` (the halt
  switch per user) and `trading_symbols` (which of the server's symbols an administrator enabled). **This is the truth for trading.**
- **Main database**: everything else. It holds a *copy* of the enabled symbols in `config.traded_symbols`, which the trader publishes (at
  every change and once a minute). The API and the live price watch read the copy. **Nothing that decides whether an order may go out is
  read from the copy**, so a changed row there can alter what the watch follows, never what is traded.
- Trading does not depend on the main database: if `tp_trader` cannot connect, the trader logs a warning and carries on.

## Settings (`deploy/.env`)

| Setting | What it is |
| --- | --- |
| `VAULT_DB_PASSWORD` | Password of the user `vault` in `vault-db`. |
| `API_DB_PASSWORD` | Password of the role `tp_api`. |
| `COLLECTOR_DB_PASSWORD` | Password of the role `tp_collector`. |
| `TRADER_MAIN_DB_PASSWORD` | Password of the role `tp_trader` in the main database. |
| `WATCH_DB_PASSWORD` | Password of the role `tp_watch` (the price watch). |
| `SIGNALS_DB_PASSWORD` | Optional. Password of the role `tp_signals` (the service that takes signals). Without it the role is not made. |
| `SIGNAL_SOURCE_DB_PASSWORD` | Optional. Password of the role `tp_signal_source` (the program that makes signals): read-only on the data tables. Without it the role is not made. |
| `MONITOR_DB_PASSWORD` | Optional. Password of the role `tp_monitor` (the monitoring agent, [monitoring.md](monitoring.md)): `pg_monitor`, so only the statistics of the server and no row of any table. Without it the role is not made. |
| `POSTGRES_PASSWORD` | As before: the main database's superuser. No program uses it. |

Each of the first five: letters and digits only, 16 or more (`openssl rand -hex 24` makes one), and all six different from each other.
`main-db-roles.sh` refuses anything else. `docker compose` refuses to start without them.

## Setting up a new server

```bash
cd /opt/tradepulse
cp deploy/.env.example deploy/.env      # fill it in, including the five passwords above
./deploy/make-jwt-keys.sh                # prints JWT_PRIVATE_KEY and JWT_PUBLIC_KEY: paste both lines into deploy/.env
alias dc='docker compose -f deploy/docker-compose.yml --env-file deploy/.env'

dc up -d postgres vault-db
./deploy/main-db-roles.sh                # makes the roles; a notice says the config tables do not exist yet: that is right
dc up -d --build api                     # its migrations make the tables, as tp_api
dc ps api                                # wait until it says "healthy"
./deploy/main-db-roles.sh                # again: now the grants on those tables; no notice this time
dc --profile public up -d --build        # the rest (leave --profile public out if you do not use Caddy)
./deploy/check-db-roles.sh               # every line "ok"
```

The order matters: the API must find its role when it starts, and the last grants can only be given once its migrations made the tables.

## Upgrading a running server

This takes a server from "everything in one database, every program the superuser" to this layout, in one go. The first steps change
nothing a running program can feel (the superuser can do everything on tables owned by someone else); trading and the **Exchange keys**
page are unavailable only between steps 6 and 7, a few minutes. The collector keeps collecting except for a moment when it restarts.

```bash
cd /opt/tradepulse
alias dc='docker compose -f deploy/docker-compose.yml --env-file deploy/.env'
git pull
```

1. **Back up, and check that the file is there.** `./deploy/backup.sh`, then `ls -lh /opt/tradepulse-backups/`: a new `tradepulse-<date>.dump`
   of some megabytes must be in the list. (The script skips the vault's database until that exists.) **Do not go on without it.** Copy it off
   the server too (see [deploy/README.md](../deploy/README.md)). Keep it until step 10.
2. **Add the four passwords** to `deploy/.env` (see Settings), and **the key pair of the tokens:** `./deploy/make-jwt-keys.sh` prints `JWT_PRIVATE_KEY`
   and `JWT_PUBLIC_KEY`; paste both lines, and delete the old `JWT_SECRET` line (it is not used any more). The private key goes only to the API, the public
   key to the vault. `docker compose` will refuse to start anything without them. **After the deploy everybody has to sign in once more:** the old tokens were
   signed another way. (The panel keeps working through its refresh tokens where it can; if a page shows an error, sign in again.)
3. **Make the roles.** `./deploy/main-db-roles.sh`. This hands the tables to their owners and gives the grants. The running programs still
   connect as the superuser and notice nothing. It prints a notice that `config.traded_symbols` does not exist yet: right, the next step makes it.
4. **Start the new API:** `dc up -d --build api`, and wait until `dc ps api` says `healthy`. It now connects as `tp_api` and its migration makes
   `config.traded_symbols`. Look at `dc logs api --tail 30`: no error.
5. **Run the roles script again:** `./deploy/main-db-roles.sh`. This time it prints no notice.
6. **Move the vault.** Stop the trader, start the vault's database, copy the schema over and the enabled symbols into the main database:

   ```bash
   dc stop trader
   dc up -d vault-db                      # wait until `dc ps vault-db` says healthy

   mkdir -p /opt/tradepulse-backups && chmod 700 /opt/tradepulse-backups
   ( umask 077; dc exec -T postgres pg_dump -U postgres -d tradepulse -n vault -Fc > /opt/tradepulse-backups/vault-move.dump )
   dc exec -T vault-db pg_restore -U vault -d vault --no-owner < /opt/tradepulse-backups/vault-move.dump

   # The same number of rows on both sides:
   for t in exchange_keys orders trading_state trading_symbols; do
     echo "$t: main=$(dc exec -T postgres psql -U postgres -d tradepulse -t -A -c "select count(*) from vault.$t") vault=$(dc exec -T vault-db psql -U vault -d vault -t -A -c "select count(*) from vault.$t")"
   done

   # So the watch does not lose the traded symbols while the trader starts:
   dc exec -T postgres psql -U postgres -d tradepulse -c \
     "INSERT INTO config.traded_symbols SELECT upper(symbol), now() FROM vault.trading_symbols WHERE enabled ON CONFLICT DO NOTHING"
   ```

   If a table is missing from the loop on your server, it has not been created yet: ignore that line. **If the counts differ, stop here.** The old
   schema is untouched and the old trader is still the one that works (`dc up -d trader` is NOT to be run with the new compose file in that case; see the
   rollback below).
7. **Start the rest on the new version:** `dc --profile public up -d --build` (without `--profile public` if you do not use Caddy). The collector
   and the trader now connect with their own roles. `dc logs trader --tail 30` and `dc logs collector --tail 30` show no error.
8. **Check the roles:** `./deploy/check-db-roles.sh`. Every line must say `ok`, and the list of connections at the end shows who is connected
   (`tp_api`, `tp_collector`, `tp_trader`, `tp_watch`, and no program as `postgres`).
9. **Check the panel.** `dc ps` shows every service `healthy`. **Exchange keys** lists your keys; **Trading** shows the same symbols enabled as before;
   **Price watch** still shows the traded symbols; switching a symbol on or off makes an entry in the audit log. The commands in
   [Looking at the databases](#looking-at-the-databases) show the same from the database side.
10. **After a few days of it working**, take the old copy out of the main database and make a fresh backup. This is the step that
    actually puts the keys out of the main database's reach:

    ```bash
    dc exec -T postgres psql -U postgres -d tradepulse -c "DROP SCHEMA vault CASCADE"
    ./deploy/backup.sh
    ls -lh /opt/tradepulse-backups/
    rm /opt/tradepulse-backups/vault-move.dump
    ```

    The older `tradepulse-*.dump` files still hold the old, encrypted copy. Delete them once you are sure of the new one, and the copies
    you sent off the server.

**You forgot the backup in step 1?** It is not lost yet: until step 10 the old `vault` schema is still whole in the main database, and
`./deploy/backup.sh` works at any moment. Run it now, check the file with `ls -lh /opt/tradepulse-backups/`, and only then go on.

**Rolling back.** Until step 10 the old data is whole. The superuser can use tables owned by anyone, so the old way of connecting still works:
check out the old version of the code, `dc up -d --build`, and the programs connect as `postgres` again. The roles and the new tables can stay;
they do no harm. What was written to the new vault database after step 6 (new orders, a halt) is not in the old one.

## Looking at the databases

Run these in the folder of the checkout (`/opt/tradepulse`), where `dc` is the alias from above. They only read.

The names that matter (the development database of old versions was called `trading_db`; **it does not exist in this stack**):

| What | Name |
| --- | --- |
| Main database: container / database / superuser | `postgres` service (`tradepulse-stack-postgres-1`) / `tradepulse` / `postgres` |
| Vault database: container / database / user | `vault-db` service (`tradepulse-stack-vault-db-1`) / `vault` / `vault` |

```bash
dc ps                                                    # every service and whether it is healthy
docker ps --format '{{.Names}}\t{{.Status}}'             # the same, with the real container names

# Main database
dc exec -T postgres psql -U postgres -d tradepulse -c '\l'      # the databases of that server
dc exec -T postgres psql -U postgres -d tradepulse -c '\dn'     # schemas: auth, config, research, public; "vault" only until step 10
dc exec -T postgres psql -U postgres -d tradepulse -c '\du'     # the roles: tp_api, tp_collector, tp_trader, tp_watch (and postgres); none of the tp_* has "Superuser"
dc exec -T postgres psql -U postgres -d tradepulse -c '\dt *.*' | grep -v pg_catalog | grep -v information_schema   # tables with their owner
dc exec -T postgres psql -U postgres -d tradepulse -c 'select "Name", left("Value", 4) as starts_with from auth.user_tokens'
                                                         # AuthenticatorKey must start with dp1: and RecoveryCodes with h1:
dc exec -T postgres psql -U postgres -d tradepulse -c 'select * from config.traded_symbols'
dc exec -T postgres psql -U postgres -d tradepulse -c 'select "OccurredAt", "UserName", "Summary", "Details" from config.audit_log order by "Id" desc limit 5'
dc exec -T postgres psql -U postgres -d tradepulse -c '\dp config.*'   # who may do what on each config table (the "Access privileges" column)

# Vault database
dc exec -T vault-db psql -U vault -d vault -c '\dt vault.*'     # exchange_keys, orders, trading_state, trading_symbols
dc exec -T vault-db psql -U vault -d vault -c 'select count(*) as keys from vault.exchange_keys'
dc exec -T vault-db psql -U vault -d vault -c 'select symbol, enabled from vault.trading_symbols'

# The roles, tried one by one with their own passwords (changes nothing)
./deploy/check-db-roles.sh
```

Without the alias, the same with Docker alone: `docker exec <container name from docker ps> psql -U postgres -d tradepulse -c '...'`
(the user and the database are those of the table above, not the container's id). Do not print the `private_key_encrypted` or `api_key` columns.

In `\dp` the access column reads `role=privileges/grantor`: `r` is SELECT, `a` INSERT, `w` UPDATE, `d` DELETE. `tp_trader=a/tp_api` on
`audit_log` means "tp_trader may INSERT, given by tp_api".

## Backups and restores

`deploy/backup.sh` writes two kinds of file into `BACKUP_DIR`: `tradepulse-*.dump` (main) and `vaultdb-*.dump` (the vault's database),
each pruned to the newest `BACKUP_KEEP`, and copied off the server like before. With encryption on (next section) the names end in `.age`
and a third kind, `api-keys-*.tgz.age`, is added. The vault dump holds the keys **encrypted**; the master
key is in neither file. `deploy/watchdog.sh` also watches `vault-db` and the age of the newest vault dump.

Restoring the **main** database: as in [deploy/README.md](../deploy/README.md), and **then run `./deploy/main-db-roles.sh` again**, and
`./deploy/check-db-roles.sh` after it. A restore with `--no-owner` makes the restoring user (`postgres`) the owner of everything and leaves the
grants out; without the script the API cannot migrate and the collector cannot write.

Restoring the **vault** database into an empty one:

```bash
dc up -d vault-db
dc exec -T vault-db pg_restore -U vault -d vault --no-owner < /opt/tradepulse-backups/vaultdb-YYYYMMDD-HHMMSS.dump
dc restart trader
```

A restored vault only works with the same `VAULT_MASTER_KEY` that encrypted its keys.

## Encrypted backups

A dump contains everything in the database: the accounts, the audit log, the configuration. It also travels: to a second place, to your
computer. Encrypting it means a stolen or leaked file is only noise. The tool is [age](https://github.com/FiloSottile/age): a key pair, where
the server only knows the **public** key (it can encrypt, never read back) and the **private** key stays with you.

**Set it up once:**

1. **On your computer**, get `age` from <https://github.com/FiloSottile/age/releases> (the Windows `.zip` has `age.exe` and `age-keygen.exe`; unzip it somewhere
   and run the programs from there). Make the key pair:

   ```powershell
   .\age-keygen.exe -o age-key.txt
   ```

   It prints `Public key: age1...`. The file `age-key.txt` is the private key. **Keep it where you keep your passwords, and print one copy on paper.
   Never put it on the server, and never in git.** Without it every backup is unreadable, and nobody, including me, can recover them.
2. **On the server:** `sudo apt install age`, and put the public key in `deploy/.env`: `BACKUP_AGE_RECIPIENT=age1...`.
3. **Run a backup by hand:** `./deploy/backup.sh`. The message ends with `encrypted` and `ls -lh /opt/tradepulse-backups/` shows files ending in `.age`
   (`tradepulse-...dump.age`, `vaultdb-...dump.age`, `api-keys-...tgz.age`). If the message says the keys of data protection were **NOT saved**, the `api`
   container was not running.
4. **Test that you can read one, now, once, before you rely on it.** On your computer:

   ```powershell
   scp "<user>@<your-server>:/opt/tradepulse-backups/tradepulse-*.dump.age" .
   .\age.exe -d -i age-key.txt -o test.dump (Get-ChildItem tradepulse-*.dump.age | Sort-Object LastWriteTime | Select-Object -Last 1).Name
   [System.Text.Encoding]::ASCII.GetString((Get-Content test.dump -Encoding Byte -TotalCount 5))
   ```

   The last line must print `PGDMP` (the first bytes of every `pg_dump` archive). Then delete `test.dump` from your computer. A backup you
   never tried to read is not a backup.
5. **Delete the old plain backups** on the server and in the places you copied them to (they are the ones that can still be read by anybody).

When `BACKUP_AGE_RECIPIENT` is empty the script works as before and writes plain files. Do not leave it empty on a server that copies backups
elsewhere. The cron job (`deploy/README.md`) needs no change.

**Restoring from an encrypted backup.** Decrypt on your computer, send the plain file to the server only for as long as the restore takes:

```powershell
.\age.exe -d -i age-key.txt -o restore.dump tradepulse-YYYYMMDD-HHMMSS.dump.age
scp restore.dump <user>@<your-server>:/tmp/restore.dump
```

```bash
# on the server (the main database; for the vault use vaultdb-... and the vault-db service as in "Backups and restores" above)
dc exec -T postgres pg_restore -U postgres -d tradepulse --no-owner < /tmp/restore.dump
rm /tmp/restore.dump          # and delete restore.dump on your computer too
./deploy/main-db-roles.sh     # a restore leaves everything owned by postgres: this hands it back
```

The keys of data protection (needed for the authenticator keys in a restored main database to be readable): decrypt `api-keys-...tgz.age`
the same way, copy it to the server, and `docker run --rm -i -v tradepulse-stack_api-keys:/keys alpine tar xzf - -C /keys < api-keys.tgz`
(find the volume name with `docker volume ls | grep api-keys`), then `dc restart api`.

**Which key to rotate when:** if the private key may have leaked, make a new pair, change `BACKUP_AGE_RECIPIENT`, and delete the old backups once a
new one exists. Old files stay readable to whoever has the old private key, so they are only worth keeping if you trust it.

## Defining a role yourself

Everything `main-db-roles.sql` does is plain SQL, and it is the template. A role in Postgres can do **nothing** until it is granted
something, and a grant names an object. Three examples, in the order of how much you give.

**A role that only reads the data tables** (the private ML project, a notebook, a Grafana):

```sql
-- as postgres, in the database tradepulse (dc exec -T postgres psql -U postgres -d tradepulse)
CREATE ROLE tp_ml LOGIN PASSWORD '...';                      -- long, random, letters and digits
GRANT CONNECT ON DATABASE tradepulse TO tp_ml;
GRANT USAGE ON SCHEMA public TO tp_ml;                       -- needed to reach anything in the schema, grants nothing by itself
GRANT SELECT ON ALL TABLES IN SCHEMA public TO tp_ml;        -- the tables that exist now
ALTER DEFAULT PRIVILEGES FOR ROLE tp_collector IN SCHEMA public GRANT SELECT ON TABLES TO tp_ml;   -- the ones the collector makes later
```

It sees the candles, funding rates, open interest and liquidations, and cannot see accounts, configuration or the vault. Reaching it from your own
computer means an SSH tunnel to the server (`ssh -L 5433:localhost:5432 ...` needs the port published; the stack does not publish it on purpose), so for
the ML project the better way is usually a copy of the data (a dump) rather than a connection.

**A role that reads one config table** (a monitoring job that needs to know what is collected):

```sql
CREATE ROLE tp_monitor LOGIN PASSWORD '...';
GRANT CONNECT ON DATABASE tradepulse TO tp_monitor;
GRANT USAGE ON SCHEMA config TO tp_monitor;
GRANT SELECT ON config.candle_worker_configurations TO tp_monitor;
```

**A role that may write one table** is a `GRANT INSERT, UPDATE` on that table, with the same two lines before it. Never give `ALL`.

Rules that keep it safe:

- **One role per program.** A role shared by two programs means the weaker one decides.
- **Grant per table** for anything that is not a plain read of the data tables. A table added later should be open to nobody until somebody decides it.
- **No `SUPERUSER`, no `CREATEDB`, no `CREATEROLE`, no table ownership** for programs that only read and write rows.
- **Look at what you made:** `\du` lists roles; `\dp config.*` and `\dp public.*` list the grants of the tables.
- **Try it with its own password, over the network,** because the database image trusts connections from inside its own container on `localhost`, so a
  test from there passes with any password. `check-db-roles.sh` does it right; by hand:

  ```bash
  PGPASSWORD='...' docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T -e PGPASSWORD postgres \
    psql -h postgres -U tp_monitor -d tradepulse -c "select 1"
  ```

  Then try what it must **not** be able to do (read a table you did not grant, write where you granted only `SELECT`) and see `permission denied`.
- **A role you add by hand is lost with a restore** of the main database that is not followed by your SQL. Keep your statements in a file next to the
  server (or add them to `deploy/main-db-roles.sql`, which is where the roles of the programs are kept), so that a restore is "restore, run the
  scripts".

A new table that the API migrates and another program needs is the only change that touches both places: add the table to a migration, add its
`GRANT` to `deploy/main-db-roles.sql` (and, if it is the collector's, to the list in that file), add a check line to `deploy/check-db-roles.sh`, and run
`./deploy/main-db-roles.sh` after the deploy.

## Changing a password

Change it in `deploy/.env`, run `./deploy/main-db-roles.sh` (it sets the new password on the role) and restart the program:

| Password | Restart |
| --- | --- |
| `API_DB_PASSWORD` | `dc up -d api` |
| `COLLECTOR_DB_PASSWORD` | `dc up -d collector` |
| `TRADER_MAIN_DB_PASSWORD` | `dc up -d trader` |
| `VAULT_DB_PASSWORD` | The password is stored in the vault's volume when the database is first made, so `.env` alone changes nothing. First `dc exec -T vault-db psql -U vault -d vault -c "ALTER USER vault PASSWORD 'the-new-one'"`, then change `.env`, then `dc up -d trader`. |

## When something does not work

| Symptom | Cause |
| --- | --- |
| Collector or API log: `permission denied for table ...` or `must be owner of table ...` | The tables are not where the script left them (a restore, a new table). Run `./deploy/main-db-roles.sh`, then `./deploy/check-db-roles.sh`. |
| API log at start: `permission denied for database` or `for schema` | The same, or the role does not exist yet: the script was not run before the API started. |
| Any program: `password authentication failed for user "tp_..."` | The password in `.env` is not the one on the role. Run `./deploy/main-db-roles.sh`, then restart the program. |
| Trader log: `Could not write the audit log entry` or `Could not publish the enabled symbols` | `tp_trader` is missing or has the wrong password, or the API has not migrated yet. Run the script. Trading is not affected; the symbols are published again every minute. |
| The price watch does not follow a symbol you enabled for trading | The copy is behind. `select * from config.traded_symbols` in the main database; it is refreshed within a minute of any change. |
| `docker compose` says `set API_DB_PASSWORD` (or another) | They are required on purpose. Add them to `deploy/.env`. |
| Trader does not start, log says it cannot connect to `vault-db` | `vault-db` is not healthy yet or the password differs. `dc ps vault-db`, `dc logs vault-db`. The trader fails closed: with no vault database nothing can trade. |
| `check-db-roles.sh` says a program still connects as `postgres` | That program was not restarted on the current `docker-compose.yml`: `dc up -d --build <service>`. |

## What this does not cover

- The tokens are signed with a key pair, and the trader has only the **public** half: a hacked trader cannot make a token, so it cannot sign in
  as anybody on the API or the panel. The other direction is still open: whoever has the API's environment (the private key) can make access and
  elevation tokens, and the trader believes them, the API could ask the trader for things. That is closed for the actions that matter: the
  trader no longer accepts the API's step-up token. Keys and orders need the **trading code**, a TOTP whose secret only the vault has
  (see [exchange-keys.md](exchange-keys.md)). A hacked API can still read the key list and the orders, halt trading and disable symbols, and the very
  first setup of a user who has no code yet is trusted. The limits are in the trader's environment, not the database.
- Someone with a shell on the server who can read the trader's environment has the master key and the vault's password.
- The superuser `postgres` can still log in from other containers with its password. No program has it, but a stronger setup would let it in only
  from the container itself (a `pg_hba.conf` of its own). Not done.
- The collector can still **drop its own tables** (it owns them). A role that only inserts would need the tables to be made by somebody else.
- The Research console (`src/Research`) and the private ML project have no role of their own yet: they use whatever connection string you
  give them. Give them the read-only role above, not `postgres`.
