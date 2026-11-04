-- The roles of the programs that use the MAIN database, and what each of them may do. Run by deploy/main-db-roles.sh (which sets the
-- three password variables below first), and by the integration tests, against the database of the stack. It can be run any number of
-- times: it creates what is missing, resets the passwords, hands tables to their owner and grants what is not granted yet.
-- Read docs/database-security.md before changing it.
--
--   tp_api        The API. Owns the schemas auth, config and research (its migrations create and change their tables) and may read the data
--                 tables. It may not write a candle.
--   tp_collector  The collector. Owns the data tables in schema public (it creates them and writes them), and reads the few config tables that
--                 say what to collect. It can see nothing of auth or research, and nothing else of config.
--   tp_trader     The key vault, which has a database of its own. Here it may only add to the audit log and publish the list of enabled symbols.
--   tp_watch      The price watch (src/Research, `watch`). Reads the symbol lists, the watch list, the enabled symbols and the stored candles; writes
--                 only its alerts (research.shock_alerts). It can see nothing of auth and cannot change the configuration.
--
-- The superuser "postgres" stays, but no program connects with it any more: it is for the backups and for running this script.
--
-- Variables (psql): api_password, collector_password, trader_password, watch_password.

-- ---- the roles (login only: no superuser, no creating databases or roles)
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', r.name, r.pw)
FROM (VALUES ('tp_api', :'api_password'), ('tp_collector', :'collector_password'), ('tp_trader', :'trader_password'), ('tp_watch', :'watch_password')) AS r(name, pw)
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = r.name) \gexec

SELECT format('ALTER ROLE %I LOGIN PASSWORD %L', r.name, r.pw)
FROM (VALUES ('tp_api', :'api_password'), ('tp_collector', :'collector_password'), ('tp_trader', :'trader_password'), ('tp_watch', :'watch_password')) AS r(name, pw) \gexec

SELECT format('GRANT CONNECT ON DATABASE %I TO tp_api, tp_collector, tp_trader, tp_watch', current_database()) \gexec
-- Only so that the API's migrations can say CREATE SCHEMA IF NOT EXISTS (Postgres checks the right even for a schema that exists).
-- It lets a role make schemas, nothing else.
SELECT format('GRANT CREATE ON DATABASE %I TO tp_api', current_database()) \gexec

-- ---- the API: its three schemas and everything in them
CREATE SCHEMA IF NOT EXISTS auth AUTHORIZATION tp_api;
CREATE SCHEMA IF NOT EXISTS config AUTHORIZATION tp_api;
CREATE SCHEMA IF NOT EXISTS research AUTHORIZATION tp_api;
ALTER SCHEMA auth OWNER TO tp_api;
ALTER SCHEMA config OWNER TO tp_api;
ALTER SCHEMA research OWNER TO tp_api;

-- ---- the tables change hands: the API's schemas to tp_api, the data tables of schema public to tp_collector. This is what an older
-- server needs once (its tables belong to postgres), and what a restore with --no-owner needs again. The sequences of identity columns
-- go with their table.
DO $$
DECLARE
    r record;
BEGIN
    FOR r IN SELECT schemaname, tablename FROM pg_tables WHERE schemaname IN ('auth', 'config', 'research') AND tableowner <> 'tp_api' LOOP
        EXECUTE format('ALTER TABLE %I.%I OWNER TO tp_api', r.schemaname, r.tablename);
    END LOOP;

    FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tableowner <> 'tp_collector' LOOP
        EXECUTE format('ALTER TABLE public.%I OWNER TO tp_collector', r.tablename);
    END LOOP;
END $$;

-- ---- the data tables: the collector makes and writes them, the API reads them (the ones made later too)
GRANT USAGE, CREATE ON SCHEMA public TO tp_collector;
GRANT USAGE ON SCHEMA public TO tp_api, tp_watch;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO tp_api, tp_watch;
ALTER DEFAULT PRIVILEGES FOR ROLE tp_collector IN SCHEMA public GRANT SELECT ON TABLES TO tp_api, tp_watch;

-- ---- the config and research tables of the collector, the trader and the watch. A table the API has not made yet (a first install, before its first start)
-- is skipped with a notice: run the script again after the API has started.
GRANT USAGE ON SCHEMA config TO tp_collector, tp_trader, tp_watch;
GRANT USAGE ON SCHEMA research TO tp_watch;

DO $$
DECLARE
    grants text[][] := ARRAY[
        -- role, privileges, table (with its schema)
        ['tp_collector', 'SELECT', 'config.candle_worker_configurations'],
        ['tp_collector', 'SELECT', 'config.symbol_data_configurations'],
        ['tp_collector', 'SELECT', 'config.exchange_symbols'],
        ['tp_collector', 'SELECT', 'config.symbols'],
        ['tp_trader', 'INSERT', 'config.audit_log'],
        ['tp_trader', 'INSERT, DELETE', 'config.traded_symbols'],
        -- the watch: the list of what to watch (and what it points at), the symbols enabled for trading, and its own alerts
        ['tp_watch', 'SELECT', 'config.shock_watch_configurations'],
        ['tp_watch', 'SELECT', 'config.exchange_symbols'],
        ['tp_watch', 'SELECT', 'config.symbols'],
        ['tp_watch', 'SELECT', 'config.exchanges'],
        ['tp_watch', 'SELECT', 'config.markets'],
        ['tp_watch', 'SELECT', 'config.traded_symbols'],
        ['tp_watch', 'SELECT, INSERT, UPDATE', 'research.shock_alerts']];
    g text[];
BEGIN
    FOREACH g SLICE 1 IN ARRAY grants LOOP
        IF to_regclass(g[3]) IS NULL THEN
            RAISE NOTICE '% does not exist yet: run this script again after the API has started', g[3];
        ELSE
            EXECUTE format('GRANT %s ON %s TO %I', g[2], g[3], g[1]);
        END IF;
    END LOOP;
END $$;
