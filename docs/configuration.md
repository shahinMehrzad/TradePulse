# Configuration in the database

[Back to the README](../README.md)

What the collector gathers is chosen in the admin panel and stored in the database, not in `appsettings.json`.

## Candle series (chosen in the database)

Which candle series (market, symbol, time frame and start date) the collector fetches is **not** in `appsettings.json`: the admin chooses them in the panel and they are stored in `config.candle_worker_configurations` (WebApi creates and migrates the `config` schema; the worker only reads it).

- The worker reads the list through the same query the admin API lists them with, and **looks at it again every 30 s**: a change made in the panel applies within about half a minute, without restarting anything. Only the workers a change concerns are touched, see [How a change is applied](#how-a-change-is-applied).
- **An empty table means the collector idles**: it has nothing to collect, checks again every 30 s and starts as soon as the first series appears, without a restart. `/health` shows `candle-series` as degraded meanwhile.
- A database that cannot be read does not stop the process either. Before any list is known the worker waits, keeps retrying every 30 s and reports `candle-series` as unhealthy. Once a list is known, a failing read changes nothing that runs: the last list stays in use, `candle-series` becomes degraded (changes made now are not seen) and the next successful read catches up.
- The tables of the series are created by the worker once it knows them. Removing a series stops fetching it and keeps its stored candles.
- **Open interest** is a flag on the same row (`CollectOpenInterest`, off by default): a series flagged on also has its open interest collected, at the same time frame. Only futures series at the time frames Binance offers can carry it (5m, 15m, 30m, 1h, 2h, 4h, 6h, 12h, 1d); the admin API refuses the rest with the reason.

## Funding rate and liquidations (chosen in the database)

They have no time frame, so they are chosen per symbol, not per candle series: one row per exchange + market + symbol in `config.symbol_data_configurations` (`CollectFundingRate` with its `FundingStartDate`, `CollectLiquidation`). The admin API manages them under `api/admin/configurations/symbol-data`, and refuses what the market does not have (both are futures-only) with the reason. They are read exactly like the candle series: an empty table makes those workers idle, and a change applies within about half a minute. Liquidations have no history, so they are only recorded from the moment they are switched on.

## How a change is applied

The two providers (`CandleSeriesProvider`, `SymbolDataProvider`) read their table every 30 s and publish the list under a new version only when it really differs (the order of the rows does not matter). Each worker takes from the list only what concerns it (the funding rate worker: the symbols that want a funding rate and from when; the open interest worker: the series flagged for it; the candle workers: all Binance series) and `PlanRunner` compares that with what it is running for:

- **The same:** nothing happens. Adding a symbol that only wants its funding rate does not touch the candle, open interest or liquidation workers.
- **Different:** the running collection of that worker is cancelled (it flushes what is queued and closes its sockets, as at shutdown) and started again for the new list. For the candle workers that means a short catch-up pass at once, then the usual boundary loop; the REST loop covers the moments a live subscription is being replaced. The liquidation stream is reconnected, and the few seconds in between are not recoverable (there is no history).
- **Nothing left:** the worker stops and waits; the tables and their stored data stay.

Tables of new series are created by the worker that starts for them. A collection that fails with an exception is not restarted: it ends the worker, as it always did, so the host stops visibly instead of carrying on half-working.

Only the request budget (`MaxLimit`) and `LiveCandles` stay in [`src/BackgroundWorker/appsettings.json`](../src/BackgroundWorker/appsettings.json):

```jsonc
"BinanceExchangeConfigurations": { "MaxLimit": 5900, "LiveCandles": true }
```
