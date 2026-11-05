# Research tools

[Back to the README](../README.md)

`src/Research` is a console program that works on the data the collector stored. It is **not** part of the collector: it runs in its own
process (or container), reads the same Postgres, and writes only to its own `research` schema, so nothing it does can slow the collection down.

```bash
dotnet run --project src/Research -- detect --symbol BTCUSDT --from 2026-01-01 [--to 2026-11-01]
dotnet run --project src/Research -- list   --symbol BTCUSDT --from 2026-01-01 [--to 2026-11-01]
dotnet run --project src/Research -- watch                                  # the symbols switched on in the admin panel
dotnet run --project src/Research -- watch  --symbols BTCUSDT[,ETHUSDT] [--market futures|spot]   # a fixed list, for trying it out
```

Connection strings come from user secrets or the environment (`ConnectionStrings__DefaultConnection`, optionally `ConnectionStrings__ResearchConnection`).

**On the server** `watch` is the `watch` service of `deploy/docker-compose.yml`: it starts with the stack, restarts with it, follows the panel's list, and stops cleanly on `docker stop`. It connects as the role `tp_watch` (`WATCH_DB_PASSWORD`, made by `deploy/main-db-roles.sh`), and its bot is `WATCH_TELEGRAM_BOT_TOKEN` / `WATCH_TELEGRAM_CHAT_ID` in `deploy/.env`. Its alerts are in `docker compose logs watch` and in `research.shock_alerts`.

## Market events (`detect`, `list`)

Finds the minutes in which the price moved far more than it usually does, from the stored 1 minute candles, and keeps what the price did afterwards.
An event is a 5 minute move of at least 1 % that is at least 5 standard deviations of the 1 minute returns of the previous 24 hours; flagged
minutes less than an hour apart are one event. It is run again over the same range without adding anything twice. Each event carries the version
of the rule that found it (`m5-0.01-z5-w1440-v2`), so events of different rules are never mixed up.

## Live watch (`watch`)

Tells you, within seconds, that the price just moved far more than it usually does. It states what happened (which way, how far, how unusual);
it never says what will happen or what to do. On a month of real trades the price went on in the same direction after such a move only about as
often as it turned back, so the direction of the *next* move cannot be read from the price alone.

- **Which symbols:** two things decide, looked at again every 30 seconds. The admin can add a symbol by hand to study it before trading it (the watch list, `config.shock_watch_configurations`), and **every symbol enabled for trading is watched on its own** (read from the copy `config.traded_symbols`, which the trading service publishes, see [trading.md](trading.md)). A row that is switched on is watched. A symbol enabled for trading is watched unless a row of kind *Trading* says off, which is what the admin does when they switch the watch of a traded symbol off, on their own responsibility; it stays off until they switch it on. A row added by hand and switched off only means "I am not studying it": if the symbol is enabled for trading afterwards, it is watched all the same, and switching it off then turns the row into that *Trading* off. A symbol that stops being traded stops being watched with nothing to clean up. The list is looked at again every 30 seconds (`Watch:Rule:WatchListRefresh`). A symbol added or switched off is started or stopped on its own; the others keep what they have learned, and an alert that is running is ended and stored. An empty list is fine: the watch waits. If the list cannot be read (the database is down), the watch goes on with the list it has. The list is apart from what the collector collects, on purpose: a symbol can be collected for months and never watched, so the chat is not filled with symbols you do not trade. A symbol can be put on the list only while its 1 minute candles are being collected, because the watch starts from them.
- **Source:** the aggregated trades of Binance over a WebSocket. Binance sells no futures candle below one minute, so 1 second bars are built from the trades (spot has 1 second candles, but futures is what the collector follows).
- **Rule:** two checks run on every symbol, each on its own, and each asks the same question: how many times bigger is this move than what **this symbol** usually does in that time (`z`)? A heavy coin and a jumpy one need no settings of their own: a move of 0.25 % in 30 seconds is 12 times usual for BTCUSDT and nothing for a coin that does 0.7 % all the time.
  - **Sudden:** the move over 30 seconds is at least **20** times usual (a crash).
  - **Gradual:** the move over 15 minutes is at least **4.5** times usual (a market that slides for ten minutes). **Use 5.0 for a quieter watch** (`Watch:Rule:GradualMinZScore`): about 1.4 alerts a week per symbol instead of 2.6, but the slide of 29 Oct 2026 is then seen in 4 symbols of 14 instead of 12.
  - "Usual" is the larger of what the last hour did and **half of what the last 24 hours did**, so a quiet night does not make a small move look huge. A move under 0.15 % is never a shock.
  - Each check has its own alert and its own rule version (`s30-z20-v2`, `m15-z4.5-v2`), so a jump that both see is two alerts. Flagged seconds less than five minutes apart (minutes, thirty) are one alert. Every number can be changed under `Watch:Rule`.
- **Start:** the usual size is estimated from the stored 1 minute candles, so the watch judges after the first 30 seconds instead of after an hour. The last hour gives the usual second (every real second then replaces a second of the estimate, on a month of data this scaling came out about 14 % above the real figure, which only makes the watch a little less sensitive at first), and the last **24 hours of candles** are the baseline of the gradual check, which then works from the start. Without recent 1 minute candles (the collector for that symbol is not running) the watch stays silent for an hour while it listens, and says so: a *warming up* message names the symbol and how long, because that silence would otherwise look like a calm market. The same message comes if the candles are older than 10 minutes (the collector is not keeping up) or cannot be read.
- **A blind watch says so:** if the connection is down for more than 15 seconds, the watch sends *Watch is blind* (and *sees the market again* when it is back), because its silence looks exactly like a calm market. If the socket queue ever fills, the dropped trades are counted in the log.
- **Alerts** are stored in `research.shock_alerts` (one row per alert, updated when it ends with its largest move), and the **start** of one is sent to the console and to the channels below: one short message, with the symbol in bold and an emoji for the direction (🟢⬆️ up, 🔴⬇️ down), the prices, how many times usual, and the time:

  ```
  🔴⬇️ SUIUSDT  -2.02 % in 15 min
  0.9217 → 0.9033 · 5.3x usual · 20:05 UTC
  ```

  The end of an alert is not told. **After a message about a symbol, no other message about it for 12 hours** (`Watch:Rule:SilenceAfterMessage`): the alerts that come meanwhile are still stored, only the message is held back, so a symbol cannot fill the chat however often it moves. Telegram gets the bold and the emoji; the console and the mail get the same words as plain text.

Reference figures behind the numbers (14 symbols, 27 days of 1 second bars from Binance's public trade files in October and November 2026, and a year of 1 minute candles for the gradual check; 28 Oct was not published by Binance). The real detector, run over those seconds with the 12 hour silence, gave an average of **2.6 messages a week per symbol** (the worst symbol 3.4), most of them from the gradual check; the sudden check alone is about 0.4 a week. Two events were replayed: the sudden crash of 7 Oct 2026 (about 02:01 UTC) would have been told for 13 of 14 symbols, and the slide of 29 Oct 2026 (about 20:05 UTC, 16:05 US Eastern) for 12 of 14, the first messages at 20:04 and 20:05. That slide was mild in price (BTCUSDT -0.6 %) and looks like the fast moves that happen a few times a week, so no setting is at once quiet and sure to catch it: that is what the 4.5 or 5.0 is about. Only two events were tried, and 4.5 was chosen after seeing one of them.

Earlier, on September 2026 BTCUSDT alone, the rule "0.3 % in 30 seconds and 6 times usual" gave about 1.5 alerts a day and caught all 9 events the 1 minute rule found, a median of about 5 minutes earlier. It was replaced because the fixed percentage floor is the same for every symbol: it silenced the heavy ones (BTCUSDT, XAUUSDT) and flooded the jumpy ones (DOTUSDT, SUIUSDT).

### Settings

Never in `appsettings.json` and never in the database; user secrets or environment variables:

| Setting | What |
| --- | --- |
| `Watch:Alerts:Telegram:BotToken`, `Watch:Alerts:Telegram:ChatId` | The watch's **own** Telegram bot, so its messages do not mix with the collector's alerts. |
| `Watch:Alerts:Email:Host`, `Port`, `UserName`, `Password`, `From`, `To` | Optional email, same shape as the collector's [alerts](alerts.md). |
| `Watch:Rule:WindowSeconds`, `MinZScore`, `GradualWindowMinutes`, `GradualMinZScore`, `MinMove`, `DayBaselineShare`, `SilenceAfterMessage`, `MergeGap`, `BlindAfter`, `SeedMinutes`, ... | The rule; see `ShockWatchOptions`. On the server only `GradualMinZScore` is passed through `deploy/docker-compose.yml`, as `WATCH_GRADUAL_MIN_Z` in `deploy/.env` (4.5, or 5.0 for a quieter watch; then `up -d`). For another one, add a line `Watch__Rule__<Name>: value` under the `watch` service. |

Without a channel the alerts only appear in the console. The table `research.shock_alerts` is created by the migration that WebApi runs at its start;
run WebApi (or apply the migration) before the first `watch`.

### What is not built yet

- Telling whether a move is in your favour or against you given an open position.
- Filling the gap after a reconnect: a hole in the trades is carried over as flat time, and a move that happened while blind is only seen as one jump afterwards.
