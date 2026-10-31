# Configuration in the database

[Back to the README](../README.md)

What the collector gathers is chosen in the admin panel and stored in the database, not in `appsettings.json`.

## Candle series (chosen in the database)

Which candle series (market, symbol, time frame and start date) the collector fetches is **not** in `appsettings.json`: the admin chooses them in the panel and they are stored in `config.candle_worker_configurations` (WebApi creates and migrates the `config` schema; the worker only reads it). A row points at a **symbol listing** (`config.exchange_symbols`: a symbol on an exchange market, with the name that exchange uses for it) and a time frame. Symbols (`config.symbols`) and listings are rows too, not code: the admin adds them through the API (see [Symbols and where they are listed](api.md#symbols-and-where-they-are-listed)) and the exchange confirms a new name before it is saved. The exchanges and markets themselves still have code behind them (`ExchangeEnum`, `MarketTypeEnum`).

- The worker reads the list through the same query the admin API lists them with, and **looks at it again every 30 s**: a change made in the panel applies within about half a minute, without restarting anything. Only the workers a change concerns are touched, see [How a change is applied](#how-a-change-is-applied).
- **An empty table means the collector idles**: it has nothing to collect, checks again every 30 s and starts as soon as the first series appears, without a restart. `/health` shows `candle-series` as degraded meanwhile.
- A database that cannot be read does not stop the process either. Before any list is known the worker waits, keeps retrying every 30 s and reports `candle-series` as unhealthy. Once a list is known, a failing read changes nothing that runs: the last list stays in use, `candle-series` becomes degraded (changes made now are not seen) and the next successful read catches up.
- The tables of the series are created by the worker once it knows them. Removing a series stops fetching it and keeps its stored candles.
- **Open interest** is a flag on the same row (`CollectOpenInterest`, off by default): a series flagged on also has its open interest collected, at the same time frame. Only futures series at the time frames Binance offers can carry it (5m, 15m, 30m, 1h, 2h, 4h, 6h, 12h, 1d); the admin API refuses the rest with the reason.
- **Market statistics** (the global long/short account ratio and the taker buy/sell volume) are one more flag on the same row (`CollectMarketStatistics`, off by default). Flagged on, the series has both collected at the same time frame, into `..._longshortratio` and `..._takervolume` tables; the rules are those of open interest.

## Funding rate and liquidations (chosen in the database)

They have no time frame, so they are chosen per symbol, not per candle series: one row per symbol listing in `config.symbol_data_configurations` (`CollectFundingRate` with its `FundingStartDate`, `CollectLiquidation`). The admin API manages them under `api/admin/configurations/symbol-data`, and refuses what the market does not have (both are futures-only) with the reason. They are read exactly like the candle series: an empty table makes those workers idle, and a change applies within about half a minute. Liquidations have no history, so they are only recorded from the moment they are switched on.

## How a change is applied

The two providers (`CandleSeriesProvider`, `SymbolDataProvider`) read their table every 30 s and publish the list under a new version only when it really differs (the order of the rows does not matter). Each worker takes from the list only what concerns it (the funding rate worker: the symbols that want a funding rate and from when; the open interest worker: the series flagged for it; the candle workers: all Binance series) and `PlanRunner` compares that with what it is running for:

- **The same:** nothing happens. Adding a symbol that only wants its funding rate does not touch the candle, open interest or liquidation workers.
- **Different:** only the part that differs is stopped and started again. The candle loops and the live candle streams are one per time frame (streams: per market and time frame), so adding a 1h series restarts the 1h loop and its stream and leaves the 4h ones running. A stopping collection flushes what is queued and closes its sockets, as at shutdown. A restarted candle loop does a short catch-up and looks for gaps only in the tables that have not been looked at in the last 24 h (the repair remembers that while the process lives), so the new series is scanned and the others are not; a socket reconnect still repairs every table. Switching open interest on or off, or changing a start date, is no concern of the candle loops and does not restart them. The liquidation stream is reconnected, and the few seconds in between are not recoverable (there is no history).
- **Nothing left:** the worker stops and waits; the tables and their stored data stay.

Tables of new series are created by the worker that starts for them. A collection that fails with an exception is not restarted: it ends the worker, as it always did, so the host stops visibly instead of carrying on half-working.

Only the request budget (`MaxLimit`) and `LiveCandles` stay in [`src/BackgroundWorker/appsettings.json`](../src/BackgroundWorker/appsettings.json):

```jsonc
"BinanceExchangeConfigurations": { "MaxLimit": 5900, "LiveCandles": true }
```
