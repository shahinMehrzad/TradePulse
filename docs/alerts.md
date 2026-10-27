# Alerts

[Back to the README](../README.md)

The collector is quiet when it is well, so a night without data would only be seen by whoever opens the console. It can send a
message to **Telegram** and/or **email** instead. Both are off until their settings are complete.

## What is announced

| When | Message |
| --- | --- |
| A health check changes (`database`, `candle-freshness`, a socket, ...) and stays changed | `⚠️ candle-freshness: degraded - 2 series behind`, `🔴 database: unhealthy - ...` |
| It is back | `✅ database: healthy again` |
| A gap appears that the exchange has no candles for | `New gap in binance_futures_btcusdt_1h: 1 gap(s), 4 candle(s) missing, from ...` |

- **Not on the first look.** A new series has its old holes found at the first repair; that is not news. Only a later repair that finds *more* is.
- **Not on a flicker.** A change is announced when the same status is seen in two health reports in a row (one every 30 s,
  `Alerts:ConfirmationsToAlert`). Each check is judged on its own, and the checks that changed together share one message.
- **Not in a flood.** At most `Alerts:MaxPerHour` (20) messages leave the collector an hour; the rest are counted and announced
  once ("Alerts were held back"). A channel that fails is logged, the other still gets the message, and nothing stops the collector.
- Alerts are sent by their own task from a small queue, so a slow Telegram or mail server cannot slow the collection.

## Settings

All under `Alerts` in the collector's `appsettings.json`, normally given as environment variables (`Alerts__Telegram__BotToken`)
or, with the Docker stack, in `deploy/.env`. **The token and the password are secrets: never put them in a file that is committed.**

| Setting | Meaning |
| --- | --- |
| `Alerts:InstanceName` | Shown in front of every message, so several collectors can be told apart (default: the machine name). |
| `Alerts:Telegram:BotToken`, `ChatId` | A bot from @BotFather, and the chat (or channel) it may write to. Both needed. |
| `Alerts:Email:Host`, `Port`, `UseStartTls`, `UserName`, `Password`, `From`, `To` | `Host`, `From` and `To` are needed. `To` may hold several addresses separated by `,` or `;`. Port 587 with STARTTLS; implicit TLS (465) is not supported. Leave `UserName` empty for a relay that takes no login. |
| `Alerts:ConfirmationsToAlert`, `Alerts:MaxPerHour` | See above. |

To find the chat id: write to the bot once, then open `https://api.telegram.org/bot<token>/getUpdates` and read `message.chat.id`.

The collector logs at start which channels it found (`Alerts go to: Telegram, Email.` or `No alert channel is configured; alerts are off.`).
The bot token is part of the Telegram address, so that client logs nothing about its requests and errors never repeat the address.
