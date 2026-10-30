# Data collection

[Back to the README](../README.md)

| Data | Source | Notes |
| --- | --- | --- |
| **Candles (OHLCV)** | REST | Spot and USD-M Futures, any configured symbol and timeframe from 1s to 1d. Resumes from the newest stored candle, repairs gaps automatically. |
| **Funding rates** | REST | Futures. Full history from a configured start date, polled hourly. |
| **Open interest** | REST | Futures, per period (5m to 1d). Binance only keeps ~30 days of history, so this must run continuously to build a long series. |
| **Long/short ratio, taker volume** | REST | Futures, per period (5m to 1d), like open interest: Binance keeps ~30 days, so they must run continuously. One admin flag (`CollectMarketStatistics`) turns on both for a series. |
| **Liquidations** | WebSocket | Futures. No history exists, so only what arrives while the worker runs is stored. Binance sends at most one liquidation per symbol per second, so this is a sample, not a complete record. |

Highlights of the collection path:

- **Idempotent, fast writes:** binary `COPY` into a temp table, then `INSERT ... ON CONFLICT DO NOTHING`, so re-fetching any range is always safe.
- **Rate-limit aware:** one shared weight limiter per market, so all collectors draw from the same Binance budget; exponential backoff on failures.
- **Self-healing data:** a daily `LAG()`-based gap detector refills missing candles. Database errors are never mistaken for an empty table. Holes the exchange itself has no candles for (maintenance, no trades) stay open and are not retried in a loop; they are logged and listed by the `candle-gaps` check of `/health`: per series how many gaps, how many candles are missing and from when. That check is always healthy, because such a hole is a fact about the data, not a fault of the collector; it is there so the people using the data can see where the holes are. The run between the start date and the first candle is not a hole: a symbol listed later simply has no candles before its listing. The exchange is asked about it once, it is reported apart (`listed-later` in the same check) and the panel offers to move the start date to the first candle.
- **Boundary-aligned scheduling:** one loop per timeframe wakes just after each candle closes.
- **Non-blocking liquidation pipeline:** the WebSocket callback only enqueues into a bounded `Channel<T>`; a single writer drains it in batches.
- **Live candles over WebSocket:** closed klines are stored the moment they close (the callback enqueues, one writer batches). The REST loop still runs at every boundary as the watchdog, and a socket reconnect triggers an immediate REST sync plus gap repair. Live candles are only accepted for a table after its first REST sync, so they can never hide missing history.
- **Config-driven:** which candle series to collect (and whether they also collect open interest, funding rate and liquidations) is chosen in the admin panel and stored in the database, no code and no redeploy (see [Candle series](configuration.md#candle-series-chosen-in-the-database)).
