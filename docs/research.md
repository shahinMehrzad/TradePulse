# Research tools

[Back to the README](../README.md)

`src/Research` is a console program that works on the data the collector stored. It is **not** part of the collector: it runs in its own
process (or container), reads the same Postgres, and writes only to its own `research` schema, so nothing it does can slow the collection down.

```bash
dotnet run --project src/Research -- detect --symbol BTCUSDT --from 2026-01-01 [--to 2026-11-01]
dotnet run --project src/Research -- list   --symbol BTCUSDT --from 2026-01-01 [--to 2026-11-01]
dotnet run --project src/Research -- watch  --symbols BTCUSDT[,ETHUSDT] [--market futures|spot]
```

Connection strings come from user secrets or the environment (`ConnectionStrings__DefaultConnection`, optionally `ConnectionStrings__ResearchConnection`).

## Market events (`detect`, `list`)

Finds the minutes in which the price moved far more than it usually does, from the stored 1 minute candles, and keeps what the price did afterwards.
An event is a 5 minute move of at least 1 % that is at least 5 standard deviations of the 1 minute returns of the previous 24 hours; flagged
minutes less than an hour apart are one event. It is run again over the same range without adding anything twice. Each event carries the version
of the rule that found it (`m5-0.01-z5-w1440-v2`), so events of different rules are never mixed up.

## Live watch (`watch`)

Tells you, within seconds, that the price just moved far more than it usually does. It states what happened (which way, how far, how unusual);
it never says what will happen or what to do. On a month of real trades the price went on in the same direction after such a move only about as
often as it turned back, so the direction of the *next* move cannot be read from the price alone.

- **Source:** the aggregated trades of Binance over a WebSocket. Binance sells no futures candle below one minute, so 1 second bars are built from the trades (spot has 1 second candles, but futures is what the collector follows).
- **Rule:** over the last 30 seconds the price moved at least 0.3 % (*attention*) or 0.5 % (*alarm*), and at least 6 standard deviations of what a second usually does in that time, measured on the previous hour. Flagged seconds less than five minutes apart are one alert. Every number can be changed under `Watch:Rule`; the rule has a version (`s30-0.003-0.005-z6-v1`) stored with each alert.
- **Start:** the usual size of a second's move is estimated from the last hour of stored 1 minute candles, so the watch judges after the first 30 seconds instead of after an hour; every real second then replaces a second of the estimate. On a month of data this scaling came out about 14 % above the real figure, which only makes the watch a little less sensitive at first. Without recent 1 minute candles (the collector for that symbol is not running) the watch stays silent for an hour while it listens, and logs that.
- **A blind watch says so:** if the connection is down for more than 15 seconds, the watch sends *Watch is blind* (and *sees the market again* when it is back), because its silence looks exactly like a calm market. If the socket queue ever fills, the dropped trades are counted in the log.
- **Alerts** are stored in `research.shock_alerts` (one row per alert, updated when it ends with its final level and largest move), and sent to the console and to the channels below. Each message gives the time, the prices, how unusual it is, the volume and the rule version.

Reference figures behind the numbers (September 2026, BTCUSDT futures, 30 days of 1 second bars built from Binance's public trade files): the 0.3 % rule
gave about 1.5 alerts a day and caught all 9 events the 1 minute rule found, a median of about 5 minutes earlier; the 0.5 % rule gave about 0.4 a day
and caught 6 of 9. A 1 % threshold missed slow, stepwise falls such as a hack, so it is not offered as a level.

### Settings

Never in `appsettings.json` and never in the database; user secrets or environment variables:

| Setting | What |
| --- | --- |
| `Watch:Alerts:Telegram:BotToken`, `Watch:Alerts:Telegram:ChatId` | The watch's **own** Telegram bot, so its messages do not mix with the collector's alerts. |
| `Watch:Alerts:Email:Host`, `Port`, `UserName`, `Password`, `From`, `To` | Optional email, same shape as the collector's [alerts](alerts.md). |
| `Watch:Rule:WindowSeconds`, `AttentionMove`, `AlarmMove`, `MinZScore`, `BaselineSeconds`, `MergeGap`, `BlindAfter`, `SeedMinutes`, ... | The rule; see `ShockWatchOptions`. |

Without a channel the alerts only appear in the console. The table `research.shock_alerts` is created by the migration that WebApi runs at its start;
run WebApi (or apply the migration) before the first `watch`.

### What is not built yet

- Enabling the watch per symbol from the admin panel (it would require the symbol's 1 minute series to be on, so there is a baseline to start from).
- Telling whether a move is in your favour or against you given an open position.
- Filling the gap after a reconnect: a hole in the trades is carried over as flat time, and a move that happened while blind is only seen as one jump afterwards.
