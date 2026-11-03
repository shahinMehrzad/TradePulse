# Databases and who may touch what

The stack has **two Postgres servers** and every program that talks to a database has its own, limited way in. This page says what
the layout is, why, how to set it up or upgrade a running server, and how to define a database role yourself.

## What it protects against

| If this happens | What the attacker gets | What stays out of reach |
| --- | --- | --- |
| A copy of the **main** database leaks (a dump, a backup) | Accounts, candles, configuration, the audit log. The second-factor secrets in it are encrypted or hashed ([deploy/README.md](../deploy/README.md)). | The encrypted private keys, the orders, the daily budget, the halt switch: they are not in that database. |
| The **API, the collector or the panel** is hacked | Whatever its own database user may do in the main database (today: a lot, see "What this does not cover"). | No network path to the vault's database and no password for it. It cannot even resolve its name. |
| The **vault's database** leaks | The private keys, still encrypted with `VAULT_MASTER_KEY`, which is only in the trader's environment. Orders and limits. | Nothing is usable without the master key. |
| The **trader** is hacked | The vault's database, the master key, and a role in the main database that can only add audit rows and publish the symbol list. | It cannot read the main database (not even the audit log it writes to), or change any account. |

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
   |                 |                 (main DB)       |
   +------ postgres -+                    ^            |
                                          |      network: vault-net (internal: no internet,
                                          |        only the trader and vault-db are on it)
                              tp_trader --+            |
                       (add audit rows,                |
                        publish symbols)            vault-db   (the vault's own Postgres)
```

| Program | Database | User | May |
| --- | --- | --- | --- |
| API, collector | main (`postgres` service, database `tradepulse`) | `postgres` | Everything (see "What this does not cover"). |
| trader | vault (`vault-db` service, database `vault`) | `vault` | Everything in its own database. It is the only one that has the password, and nothing else is stored there. |
| trader | main | `tp_trader` | `INSERT` on `config.audit_log`; `INSERT`, `DELETE` on `config.traded_symbols`; `USAGE` on schema `config`. It cannot `SELECT` anything, `UPDATE`, or create tables. |
| panel | none | none | The panel has no database. It asks the API and the trader. |

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
| `VAULT_DB_PASSWORD` | Password of the user `vault` in `vault-db`. Letters and digits, `openssl rand -hex 24`. |
| `TRADER_MAIN_DB_PASSWORD` | Password of the role `tp_trader` in the main database. Letters and digits (the script refuses anything else, and anything shorter than 16). |
| `POSTGRES_PASSWORD` | As before: the main database's superuser. Keep all three different from each other. |

The trader's two connection strings (`ConnectionStrings__Vault`, `ConnectionStrings__Main`) are built in `deploy/docker-compose.yml` from these.
`ConnectionStrings__Main` is optional for the program: left out, it uses the vault database for the audit log and the copy as well
(development and tests do that).

## Setting up a new server

```bash
cd /opt/tradepulse
cp deploy/.env.example deploy/.env      # fill it in, including the two passwords above
alias dc='docker compose -f deploy/docker-compose.yml --env-file deploy/.env'

dc up -d --build postgres api            # the API's migrations make the tables the role needs
dc ps api                                # wait until it says "healthy"
./deploy/main-db-roles.sh                # makes the role tp_trader in the main database
dc --profile public up -d --build        # now the rest (leave --profile public out if you do not use Caddy)
```

Doing the two steps in this order matters: if the trader starts before the role exists, the audit entries of those minutes are lost
(the trader logs it) and the symbol copy waits for the next minute.

## Upgrading a running server (the vault was in the main database)

Before this change the key vault lived in the schema `vault` of the main database. The steps move it to `vault-db`. Trading and the
**Exchange keys** page are unavailable between steps 5 and 8 (a few minutes); nothing else stops. The collector keeps running.

```bash
cd /opt/tradepulse
alias dc='docker compose -f deploy/docker-compose.yml --env-file deploy/.env'
git pull
```

1. **Back up, and check that the file is there.** `./deploy/backup.sh`, then `ls -lh /opt/tradepulse-backups/`: a new `tradepulse-<date>.dump`
   of some megabytes must be in the list. (The script skips the vault's database until that exists.) Do not go on without it. Keep this dump until step 10.
2. **Add the two passwords** to `deploy/.env` (see Settings). `docker compose` will refuse to start without them.
3. **Deploy the API only**, so the migration makes `config.traded_symbols`:
   `dc up -d --build api` and wait for `dc ps api` to say `healthy`.
4. **Make the role:** `./deploy/main-db-roles.sh` (it prints `Role tp_trader is ready`).
5. **Stop the trader:** `dc stop trader`.
6. **Start the vault's database:** `dc up -d vault-db`, wait for `healthy`.
7. **Copy the vault schema across, and the enabled symbols into the main database:**

   ```bash
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

   If a table is missing from the loop on your server, that table has not been created yet: ignore it. If the counts differ, stop here
   and do not go on (the old schema is still the one in use until step 8 starts the new trader).
8. **Start the rest on the new code:** `dc --profile public up -d --build` (without `--profile public` if you do not use Caddy).
   Look at `docker logs tradepulse-stack-trader-1 --tail 30`: there must be no error about the database.
9. **Check it works.** `dc ps` shows every service `healthy`. `dc logs trader --tail 30` shows no error. Then the commands in
   [Looking at the databases](#looking-at-the-databases): the vault's database must hold your keys and `config.traded_symbols` must list the enabled symbols.
   In the panel: **Exchange keys** lists your keys; **Trading** shows the same symbols enabled as before; **Price watch** still shows the traded
   symbols; switching a symbol on or off makes an entry in the audit log.
10. **After a few days of it working**, take the old copy out of the main database and make a fresh backup. This is the step that
    actually puts the keys out of the main database's reach:

    ```bash
    dc exec -T postgres psql -U postgres -d tradepulse -c "DROP SCHEMA vault CASCADE"
    ./deploy/backup.sh
    rm /opt/tradepulse-backups/vault-move.dump
    ```

    The older `tradepulse-*.dump` files still hold the old, encrypted copy. Delete them when you are sure of the new ones (and the copies
    you sent off the server).

**You forgot the backup in step 1?** It is not lost yet: until step 10 the old `vault` schema is still whole in the main database, and
`./deploy/backup.sh` works at any moment. Run it now, check the file with `ls -lh /opt/tradepulse-backups/`, and only then go on.

**If something goes wrong before step 10:** the old schema is untouched in the main database. Stop the new trader, put the old
`ConnectionStrings__Vault` back (the main database: `Host=postgres;Database=tradepulse;Username=postgres;Password=...`), check out the old
code, and `dc up -d --build trader`. Whatever was done in the new vault database after step 8 (new orders, a halt) is not in the old one.

## Looking at the databases

Run these in the folder of the checkout (`/opt/tradepulse`), where `dc` is the alias from above. They only read.

The names that matter (the development database of old versions was called `trading_db`; **it does not exist in this stack**):

| What | Name |
| --- | --- |
| Main database: container / database / user | `postgres` service (`tradepulse-stack-postgres-1`) / `tradepulse` / `postgres` |
| Vault database: container / database / user | `vault-db` service (`tradepulse-stack-vault-db-1`) / `vault` / `vault` |

```bash
dc ps                                                    # every service and whether it is healthy
docker ps --format '{{.Names}}\t{{.Status}}'             # the same, with the real container names

# Main database
dc exec -T postgres psql -U postgres -d tradepulse -c '\l'      # the databases of that server
dc exec -T postgres psql -U postgres -d tradepulse -c '\dn'     # schemas: auth, config, research, public; "vault" only until step 10
dc exec -T postgres psql -U postgres -d tradepulse -c 'select "Name", left("Value", 4) as starts_with from auth.user_tokens'
                                                         # AuthenticatorKey must start with dp1: and RecoveryCodes with h1:
dc exec -T postgres psql -U postgres -d tradepulse -c 'select * from config.traded_symbols'
dc exec -T postgres psql -U postgres -d tradepulse -c 'select "OccurredAt", "UserName", "Summary", "Details" from config.audit_log order by "Id" desc limit 5'

# Vault database
dc exec -T vault-db psql -U vault -d vault -c '\dt vault.*'     # exchange_keys, orders, trading_state, trading_symbols
dc exec -T vault-db psql -U vault -d vault -c 'select count(*) as keys from vault.exchange_keys'
dc exec -T vault-db psql -U vault -d vault -c 'select symbol, enabled from vault.trading_symbols'
```

Without the alias, the same with Docker alone: `docker exec <container name from docker ps> psql -U postgres -d tradepulse -c '...'`
(the user and the database are those of the table above, not the container's id). Do not print the `private_key_encrypted` or `api_key` columns.

## Backups and restores

`deploy/backup.sh` now writes two kinds of file into `BACKUP_DIR`: `tradepulse-*.dump` (main) and `vaultdb-*.dump` (the vault's database),
each pruned to the newest `BACKUP_KEEP`, and copied off the server like before. The vault dump holds the keys **encrypted**; the master
key is in neither file. `deploy/watchdog.sh` also watches `vault-db` and the age of the newest vault dump.

Restoring the **main** database: as in [deploy/README.md](../deploy/README.md), and **then run `./deploy/main-db-roles.sh` again**. A
restore with `--no-owner` leaves the grants out, and without them the trader cannot write the audit log.

Restoring the **vault** database into an empty one:

```bash
dc up -d vault-db
dc exec -T vault-db pg_restore -U vault -d vault --no-owner < /opt/tradepulse-backups/vaultdb-YYYYMMDD-HHMMSS.dump
dc restart trader
```

A restored vault only works with the same `VAULT_MASTER_KEY` that encrypted its keys.

## Defining a role yourself

Everything `main-db-roles.sh` does is plain SQL; it is the template. A role in Postgres can do **nothing** until it is granted something,
and a grant names an object. To give a program a narrow way into the main database:

```sql
-- as postgres, in the database tradepulse
CREATE ROLE tp_example LOGIN PASSWORD '...';                 -- a long random password, letters and digits
GRANT CONNECT ON DATABASE tradepulse TO tp_example;
GRANT USAGE ON SCHEMA config TO tp_example;                  -- needed to reach anything in the schema, grants nothing by itself
GRANT SELECT ON config.some_table TO tp_example;             -- one grant per table and per kind of access
-- kinds of access: SELECT, INSERT, UPDATE, DELETE. Give only the ones the program really uses.
```

Rules that keep it safe:

- **One role per program.** A role shared by two programs means the weaker one decides.
- **Grant per table, never `ALL TABLES IN SCHEMA`** for a new role: a table added later would be open to it without anybody deciding that.
- **No `SUPERUSER`, no `CREATEDB`, no `CREATEROLE`, no table ownership** for programs that only read and write rows.
- **Check what you made:** `\du` lists roles; `\dp config.*` lists the grants of every table in the schema (the last column is who may do what).
- **Test it from outside the container**, over the network, because the database image trusts connections from inside its own container
  and a test from there would pass with any password:

  ```bash
  docker run --rm --network tradepulse-stack_default -e PGPASSWORD='...' postgres:18-alpine \
    psql -h postgres -U tp_example -d tradepulse -c "select 1"
  ```

  Then try what it must **not** be able to do (read a table you did not grant, write where you granted only `SELECT`) and see `permission denied`.
- If you add a new table that an existing role needs, add its grant to the script **and** run the script: the grants are part of the deploy, and they
  are lost with a restore.

A new table that the API migrates and the trader needs is the only change that touches both places. Add the table to a migration, add the
`GRANT` to `deploy/main-db-roles.sh`, and run the script after the deploy.

## Changing a password

- **`tp_trader`:** change `TRADER_MAIN_DB_PASSWORD` in `deploy/.env`, run `./deploy/main-db-roles.sh` (it sets the new password on the role),
  then `dc up -d trader`.
- **`vault`:** the password is stored in the vault volume when the database is first made, so changing `VAULT_DB_PASSWORD` in `.env` alone
  does not change it. Run `dc exec -T vault-db psql -U vault -d vault -c "ALTER USER vault PASSWORD 'the-new-one'"`, change `.env`, then `dc up -d trader`.

## When something does not work

| Symptom | Cause |
| --- | --- |
| Trader log: `Could not write the audit log entry` with `permission denied` or `password authentication failed` | The role is missing or its password differs from `.env`. Run `./deploy/main-db-roles.sh`. Trading is not affected. |
| Trader log: `Could not publish the enabled symbols to the main database` | Same, or the API has not migrated yet (`config.traded_symbols` does not exist). The trader tries again every minute. |
| The price watch does not follow a symbol you enabled for trading | The copy is behind. Look at `select * from config.traded_symbols` in the main database; it is refreshed within a minute of any change. |
| `docker compose` says `set VAULT_DB_PASSWORD` / `set TRADER_MAIN_DB_PASSWORD` | They are required on purpose. Add them to `deploy/.env`. |
| Trader does not start, log says it cannot connect to `vault-db` | `vault-db` is not healthy yet or the password differs. `dc ps vault-db`, `dc logs vault-db`. The trader fails closed: with no vault database nothing can trade. |

## What this does not cover

- The **API and the collector still connect to the main database as the superuser `postgres`**. A hacked collector can read the accounts
  and the second-factor secrets (encrypted, but the API's data protection keys are the API's, not the collector's). The next step is a
  limited role for each (`tp_api`, `tp_collector`); it is more work because the collector makes its own tables, and is not done.
- The trader still believes the **same `JWT_SECRET`** as the API. Whoever has the API's environment can make access and elevation tokens
  and use the keys within the limits (order size, daily total, symbols, halt). The limits are in the trader's environment, not the database.
- Someone with a shell on the server who can read the trader's environment has the master key and the vault's password.
