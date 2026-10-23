# 📈 TradePulse: Market Data Collector & Analytics Platform

[![CI](https://github.com/shahinMehrzad/TradePulse/actions/workflows/ci.yml/badge.svg)](https://github.com/shahinMehrzad/TradePulse/actions/workflows/ci.yml)

A .NET 10 / C# 14 project with two purposes: a **reference for how I structure and write code**, and a **real market-data collector** that feeds my own machine-learning trading research.

It continuously collects Binance market data into PostgreSQL, built for speed and reliability on the collection path, with a Clean Architecture around it.

> **Status:** the data collector is the focus and is working. The Web API provides authentication and a read-only candle endpoint; the UI, messaging and caching pieces are planned (see the [roadmap](#-roadmap)). Everything listed under "Implemented" exists in the code today.

## ✅ Implemented

### Data collection (`BackgroundWorker`)

| Data | Source | Notes |
| --- | --- | --- |
| **Candles (OHLCV)** | REST | Spot and USD-M Futures, any configured symbol and timeframe from 1s to 1d. Resumes from the newest stored candle, repairs gaps automatically. |
| **Funding rates** | REST | Futures. Full history from a configured start date, polled hourly. |
| **Open interest** | REST | Futures, per period (5m to 1d). Binance only keeps ~30 days of history, so this must run continuously to build a long series. |
| **Liquidations** | WebSocket | Futures. No history exists, so only what arrives while the worker runs is stored. Binance sends at most one liquidation per symbol per second, so this is a sample, not a complete record. |

Highlights of the collection path:

- **Idempotent, fast writes:** binary `COPY` into a temp table, then `INSERT ... ON CONFLICT DO NOTHING`, so re-fetching any range is always safe.
- **Rate-limit aware:** one shared weight limiter per market, so all collectors draw from the same Binance budget; exponential backoff on failures.
- **Self-healing data:** a daily `LAG()`-based gap detector refills missing candles. Database errors are never mistaken for an empty table.
- **Boundary-aligned scheduling:** one loop per timeframe wakes just after each candle closes.
- **Non-blocking liquidation pipeline:** the WebSocket callback only enqueues into a bounded `Channel<T>`; a single writer drains it in batches.
- **Live candles over WebSocket:** closed klines are stored the moment they close (the callback enqueues, one writer batches). The REST loop still runs at every boundary as the watchdog, and a socket reconnect triggers an immediate REST sync plus gap repair. Live candles are only accepted for a table after its first REST sync, so they can never hide missing history.
- **Config-driven:** which candle series to collect is chosen in the admin panel and stored in the database, no code and no redeploy (see [Candle series](#candle-series-chosen-in-the-database)).


### Health monitoring

The worker serves `GET /health` (JSON, `503` when anything is unhealthy) and `GET /health/live` on `http://localhost:5080` (configurable under `Health` in `appsettings.json`; use `http://0.0.0.0:8080` inside a container). It reports:

| Check | What it proves |
| --- | --- |
| `database` | PostgreSQL accepts connections and answers a query. |
| `binance-futures-rest`, `binance-spot-rest` | A probe pings the REST APIs every 30 s, independent of candle schedules. Failures escalate from degraded to unhealthy after 3 in a row. |
| `binance-futures-websocket` | A busy probe stream (mark price) is open and flowing. Silence for 60 s or a disconnect longer than 2 min is unhealthy. |
| `binance-liquidation-stream` | The real liquidation subscription is connected. Liquidations are bursty, so a long silence (default 60 min) only degrades. |
| `binance-candle-stream-futures`, `binance-candle-stream-spot` | The live candle subscription is connected and receiving (5 min of silence degrades). Turn the stream off with `LiveCandles: false`. |
| `candle-series` | The worker knows what to collect. Healthy with series, degraded when none are configured (idle), unhealthy when the database cannot be read. |
| `candle-freshness` | The newest stored candle of every table is not behind the clock (1 candle behind is degraded, 3 or more unhealthy; a table that is still moving during backfill is healthy). |
| `candle-requests`, `funding-rate-requests`, `open-interest-requests` | Consecutive failures of the collectors' own API requests. |

Funding rate, open interest and liquidation checks exist only when those sections are configured. The candle checks are always there, because the series are chosen at run time; the per-market stream checks and the spot ping only judge a market that has a series. Status changes are also written to the log, so a healthy worker stays quiet and a problem is visible without polling the endpoint.

### Architecture

Clean Architecture, dependencies pointing inward:

```
Domain  ←  Application  ←  Infrastructure  ←  BackgroundWorker / WebApi / WebRazor
```

- **Domain:** entities, value objects (`Price`, `TimeRange`), enums, repository interfaces.
- **Application:** a small hand-written CQRS layer (`ICommandHandler`, `ICommandDispatcher`, handlers discovered by reflection), and the `TableNameRegistry`.
- **Infrastructure:** Dapper and Npgsql for market data, EF Core with ASP.NET Identity for authentication.
- **WebApi:** JWT login and refresh tokens, and the candle read API below. **WebRazor:** admin panel (sign-in, a dashboard and the list of candle series to collect), see [Admin panel](#admin-panel-webrazor).

### Candle read API (`WebApi`)

Read-only access to the stored closed candles; the route names the series and the table name is derived from the parsed enums only. **Requires a bearer token** (`POST /api/auth/login`, then `Authorization: Bearer <accessToken>`).

| Request | Returns |
|---|---|
| `GET /api/candles/binance/futures/btcusdt/4h?from=&to=&limit=` | Candles in a range, oldest first, one page (default 500, max 1000). Follow `nextFrom` for the next page. |
| `GET /api/candles/binance/futures/btcusdt/4h/latest?limit=78&to=` | The newest N candles up to an optional time, oldest first: the lookback window an indicator such as Ichimoku needs. |

Times are ISO 8601 UTC (offset-less input is read as UTC). Bad input is a 400 with the offending parameter, a series the collector never created is a 404. The endpoints talk to `ICandleReader`, so the cache is a decorator behind it.

### Authentication

`POST /api/auth/login` issues a JWT; the candle endpoints validate its signature (HMAC-SHA256 only), issuer, audience and lifetime (30 s clock skew) and answer 401 otherwise. The signing secret and the seeded admin credentials are **not** in the committed `appsettings.json`: Development gets throwaway values from `appsettings.Development.json`; anywhere else supply `JwtConfigurations__Secret` (at least 32 bytes) and, to seed an admin, `AdminConfigurations__Email` and `AdminConfigurations__Password` as environment variables. The host refuses to start without a strong secret instead of running with forgeable tokens. Never run a public deployment with `ASPNETCORE_ENVIRONMENT=Development`.

`POST /api/auth/refresh-token` with `{ "refreshToken": "..." }` (and the same `X-Device-Id` header used at login) returns a new access token and a new refresh token. It needs no access token, since that is usually expired by then. Refresh tokens are single-use (rotated), bound to the device they were issued to, expire after `RefreshTokenDurationInDays`, and only their SHA-256 hash is stored. Concurrent use of one token succeeds exactly once.

### Admin panel (`WebRazor`)

A Tabler-based panel that only the seeded administrator can enter. It references no other project and computes nothing: every page asks WebApi over HTTP (`Api:BaseUrl`). Sign-in posts to `POST /api/auth/login`, then `GET /api/admin/me` (which needs the admin role and answers 403 to anyone else) decides whether the user may enter. The API tokens stay on the panel's server (`SessionTokenStore`, in memory, so a restart signs the admin out); the browser only gets an `HttpOnly`, `SameSite=Strict` cookie holding an opaque session id, and the access token is refreshed shortly before it expires. Sessions last 8 hours.

The **Collector series** page lists the candle series stored through the API (`/api/admin/configurations/...`) and adds, changes and removes them. Dropdowns come from the public `/api/lookups/...` lists, which mirror the enums. The collector collects exactly this list, see [Candle series](#candle-series-chosen-in-the-database).

WebApi side: the `Admin` policy (role from `AdminConfigurations:Role`) guards `/api/admin/*`, `POST /api/auth/login` is limited to `RateLimiting:LoginPermitsPerMinute` (default 10) per client IP, and CORS stays off until `Cors:AllowedOrigins` lists origins. The panel itself needs no CORS since it calls the API from the server.

### Candle cache

`CachedCandleReader` decorates `ICandleReader` with .NET's `HybridCache`: entries live in process memory (L1) and, if `ConnectionStrings:Redis` is set, also in Redis (L2) so several API instances share them. Concurrent identical requests share one database call, and errors are never cached. Answers that reach the newest candle expire after `CandleCache:LatestTtl` (30 s); answers that end more than two candles ago expire after `HistoricalTtl` (1 h, not forever, because Binance sometimes revises old candles). Set `CandleCache:Enabled` to `false` to turn it off.

Measured (BenchmarkDotNet, Postgres and Redis in Docker on the same machine, Ryzen 7 8840HS; warm cache, mean):

| Request | No cache | Redis only (L2) | Memory (L1) |
|---|---|---|---|
| latest 78 x 4h (Ichimoku lookback) | 959 us | 821 us | 0.30 us |
| latest 1 x 15m | 809 us | 565 us | 0.31 us |
| page of 500 x 15m | 1,575 us | 2,005 us | 0.33 us |

Takeaways: Postgres already answers in about 1 ms, so a Redis hit saves little (a network round trip either way) and a large page is even slower from Redis because of the JSON copy. The gain comes from the in-memory level; Redis only adds sharing between instances. Redis is opt-in: if it is configured and goes down, requests still succeed but every cache miss waits for the Redis timeout, so use short ones, e.g. `localhost:6379,abortConnect=false,connectTimeout=500,syncTimeout=500,asyncTimeout=500`.

### Quality

- **Unit tests** (xunit v3): value objects, command handling, rate limiter and health checks (with `FakeTimeProvider`), scheduling, the real health endpoint on Kestrel, table definitions and **contract tests** that fail if an enum value, table name or `appsettings.json` entry drifts from what other services expect.
- **Integration tests** (Testcontainers, real Postgres, needs Docker): the repositories' SQL, idempotent inserts, rollback, gap detection and the read queries.
- **API integration tests** (`WebApi.IntegrationTests`, the real host on a throwaway Postgres): login, bearer validation and every rejection case (no token, wrong key, issuer or audience, expired, tampered, `alg: none`, wrong algorithm), and the startup refusal without a strong secret.
- **Benchmarks** (BenchmarkDotNet, `benchmarks/TradePulse.Benchmarks`, needs Docker): read latency of the candle query layer against a seeded Postgres (300k 15m + 18.7k 4h candles), with and without the cache. Run with `dotnet run -c Release --project benchmarks/TradePulse.Benchmarks -- --filter '*'`. Results are in [Candle cache](#candle-cache).
- **CI:** GitHub Actions and Azure Pipelines both build and test every push and pull request.

## 🧩 Design decisions

- **The collector is a separate concern.** It runs as its own service so a bad request elsewhere can never slow or stop data collection. Other services share the same PostgreSQL schema.
- **The schema and enums are a contract.** One table per exchange, market, symbol and timeframe, named by a single registry (`binance_futures_btcusdt_4h`, `binance_futures_btcusdt_fundingrate`, `binance_futures_btcusdt_1h_openinterest`, `binance_futures_btcusdt_liquidation`). Enum numeric values are never renumbered.
- **Time columns are `timestamp` and always UTC.**

## 🚀 Getting started

Requirements: .NET 10 SDK, Docker.

```bash
docker-compose up -d                  # PostgreSQL (plus Redis and RabbitMQ for upcoming features)
dotnet run --project src/BackgroundWorker
```

### Candle series (chosen in the database)

Which candle series (market, symbol, time frame and start date) the collector fetches is **not** in `appsettings.json`: the admin chooses them in the panel and they are stored in `config.candle_worker_configurations` (WebApi creates and migrates the `config` schema; the worker only reads it).

- The worker reads the list when it starts, through the same query the admin API lists them with. **A change applies when the worker is restarted.**
- **An empty table means the collector idles**: it has nothing to collect, checks again every 30 s and starts as soon as the first series appears, without a restart. `/health` shows `candle-series` as degraded meanwhile.
- A database that cannot be read does not stop the process either: the worker waits, keeps retrying every 30 s and reports `candle-series` as unhealthy.
- The tables of the series are created by the worker once it knows them. Removing a series stops fetching it and keeps its stored candles.
- **Open interest** is a flag on the same row (`CollectOpenInterest`, off by default): a series flagged on also has its open interest collected, at the same time frame. Only futures series at the time frames Binance offers can carry it (5m, 15m, 30m, 1h, 2h, 4h, 6h, 12h, 1d); the admin API refuses the rest with the reason.

Funding rate and liquidations are still configured in [`src/BackgroundWorker/appsettings.json`](src/BackgroundWorker/appsettings.json), together with the request budget (`MaxLimit`) and `LiveCandles`:

```jsonc
"BinanceExchangeConfigurations": { "MaxLimit": 5900, "LiveCandles": true },
"BinanceFuturesDataConfigurations": {
  "FundingRates": { "BTCUSDT": { "Name": "BTCUSDT", "StartDate": "2020-01-01T00:00:00Z" } },
  "Liquidations": { "BTCUSDT": { "Name": "BTCUSDT" } }
}
```

Run the tests:

```bash
dotnet test --solution DotNetTradePulse.slnx
```

## 🗺️ Roadmap

Planned, not built yet:

- Event publishing (`CandleClosed`) through **RabbitMQ** using an outbox, and **Redis** for latest-candle caching.
- Candle read API extras (streaming, CSV/Parquet export, cache invalidation from `CandleClosed` events instead of TTL) and a data-health dashboard.
- Re-checking recent candles for exchange-side revisions.
- More exchanges and symbols behind a common collector abstraction.
- BenchmarkDotNet results, OpenTelemetry.

## 📄 License

MIT, see [LICENSE](LICENSE).
