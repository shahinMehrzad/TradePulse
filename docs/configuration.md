# Configuration in the database

[Back to the README](../README.md)

What the collector gathers is chosen in the admin panel and stored in the database, not in `appsettings.json`.

## Candle series (chosen in the database)

Which candle series (market, symbol, time frame and start date) the collector fetches is **not** in `appsettings.json`: the admin chooses them in the panel and they are stored in `config.candle_worker_configurations` (WebApi creates and migrates the `config` schema; the worker only reads it).

- The worker reads the list when it starts, through the same query the admin API lists them with. **A change applies when the worker is restarted.**
- **An empty table means the collector idles**: it has nothing to collect, checks again every 30 s and starts as soon as the first series appears, without a restart. `/health` shows `candle-series` as degraded meanwhile.
- A database that cannot be read does not stop the process either: the worker waits, keeps retrying every 30 s and reports `candle-series` as unhealthy.
- The tables of the series are created by the worker once it knows them. Removing a series stops fetching it and keeps its stored candles.
- **Open interest** is a flag on the same row (`CollectOpenInterest`, off by default): a series flagged on also has its open interest collected, at the same time frame. Only futures series at the time frames Binance offers can carry it (5m, 15m, 30m, 1h, 2h, 4h, 6h, 12h, 1d); the admin API refuses the rest with the reason.

## Funding rate and liquidations (chosen in the database)

They have no time frame, so they are chosen per symbol, not per candle series: one row per exchange + market + symbol in `config.symbol_data_configurations` (`CollectFundingRate` with its `FundingStartDate`, `CollectLiquidation`). The admin API manages them under `api/admin/configurations/symbol-data`, and refuses what the market does not have (both are futures-only) with the reason. They are read exactly like the candle series: when the worker starts, an empty table makes those workers idle, and a change applies on restart. Liquidations have no history, so they are only recorded from the moment the worker runs.

Only the request budget (`MaxLimit`) and `LiveCandles` stay in [`src/BackgroundWorker/appsettings.json`](../src/BackgroundWorker/appsettings.json):

```jsonc
"BinanceExchangeConfigurations": { "MaxLimit": 5900, "LiveCandles": true }
```
