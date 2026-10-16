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
- **Config-driven:** adding a symbol or timeframe is an `appsettings.json` change, no code.


### Health monitoring

The worker serves `GET /health` (JSON, `503` when anything is unhealthy) and `GET /health/live` on `http://localhost:5080` (configurable under `Health` in `appsettings.json`; use `http://0.0.0.0:8080` inside a container). It reports:

| Check | What it proves |
| --- | --- |
| `database` | PostgreSQL accepts connections and answers a query. |
| `binance-futures-rest`, `binance-spot-rest` | A probe pings the REST APIs every 30 s, independent of candle schedules. Failures escalate from degraded to unhealthy after 3 in a row. |
| `binance-futures-websocket` | A busy probe stream (mark price) is open and flowing. Silence for 60 s or a disconnect longer than 2 min is unhealthy. |
| `binance-liquidation-stream` | The real liquidation subscription is connected. Liquidations are bursty, so a long silence (default 60 min) only degrades. |
| `binance-candle-stream-futures`, `binance-candle-stream-spot` | The live candle subscription is connected and receiving (5 min of silence degrades). Turn the stream off with `LiveCandles: false`. |
| `candle-freshness` | The newest stored candle of every table is not behind the clock (1 candle behind is degraded, 3 or more unhealthy; a table that is still moving during backfill is healthy). |
| `candle-requests`, `funding-rate-requests`, `open-interest-requests` | Consecutive failures of the collectors' own API requests. |

Only components that are actually configured get a check. Status changes are also written to the log, so a healthy worker stays quiet and a problem is visible without polling the endpoint.

### Architecture

Clean Architecture, dependencies pointing inward:

```
Domain  ←  Application  ←  Infrastructure  ←  BackgroundWorker / WebApi / WebRazor
```

- **Domain:** entities, value objects (`Price`, `TimeRange`), enums, repository interfaces.
- **Application:** a small hand-written CQRS layer (`ICommandHandler`, `ICommandDispatcher`, handlers discovered by reflection), and the `TableNameRegistry`.
- **Infrastructure:** Dapper and Npgsql for market data, EF Core with ASP.NET Identity for authentication.
- **WebApi:** JWT login and refresh tokens, and the candle read API below. **WebRazor:** placeholder UI.

### Candle read API (`WebApi`)

Read-only access to the stored closed candles; the route names the series and the table name is derived from the parsed enums only.

| Request | Returns |
|---|---|
| `GET /api/candles/binance/futures/btcusdt/4h?from=&to=&limit=` | Candles in a range, oldest first, one page (default 500, max 1000). Follow `nextFrom` for the next page. |
| `GET /api/candles/binance/futures/btcusdt/4h/latest?limit=78&to=` | The newest N candles up to an optional time, oldest first: the lookback window an indicator such as Ichimoku needs. |

Times are ISO 8601 UTC (offset-less input is read as UTC). Bad input is a 400 with the offending parameter, a series the collector never created is a 404. The endpoints talk to `ICandleReader`, so a caching decorator can be added without touching them.

### Quality

- **Unit tests** (xunit v3): value objects, command handling, rate limiter and health checks (with `FakeTimeProvider`), scheduling, the real health endpoint on Kestrel, table definitions and **contract tests** that fail if an enum value, table name or `appsettings.json` entry drifts from what other services expect.
- **Integration tests** (Testcontainers, real Postgres, needs Docker): the repositories' SQL, idempotent inserts, rollback, gap detection and the read queries.
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

Symbols, timeframes and start dates are configured in [`src/BackgroundWorker/appsettings.json`](src/BackgroundWorker/appsettings.json):

```jsonc
"BinanceExchangeConfigurations": { "Symbols": { "Futures_BTCUSDT_4H": { "Name": "BTCUSDT", "Interval": "_4H", "MarketType": "Futures", "StartDate": "2020-01-01T00:00:00Z" } } },
"BinanceFuturesDataConfigurations": {
  "FundingRates": { "BTCUSDT": { "Name": "BTCUSDT", "StartDate": "2020-01-01T00:00:00Z" } },
  "OpenInterests": { "BTCUSDT_1H": { "Name": "BTCUSDT", "Interval": "_1H" } },
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
- Candle read API extras (hybrid caching, streaming, CSV/Parquet export) and a data-health dashboard.
- Re-checking recent candles for exchange-side revisions.
- More exchanges and symbols behind a common collector abstraction.
- Integration tests with Testcontainers, BenchmarkDotNet results, OpenTelemetry.

## 📄 License

MIT, see [LICENSE](LICENSE).
