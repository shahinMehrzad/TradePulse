# Health monitoring

[Back to the README](../README.md)

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

Every check is registered from the start, because what to collect is chosen at run time (in the database): the ones that depend on the choice (the per-market candle streams, the spot ping, the liquidation stream) only judge what is actually collected and otherwise report "not in use". Status changes are also written to the log, so a healthy worker stays quiet and a problem is visible without polling the endpoint.

In addition, every `Health:SummaryIntervalMinutes` (default 10, 0 turns it off) the worker writes one summary line, since the collectors log nothing per batch: what was stored in that period, how far the newest candle is behind and how many live streams are up. The `Logging` section of `appsettings.json` keeps the libraries' per-request lines (HTTP client, SQL) at Warning so the log stays readable.
