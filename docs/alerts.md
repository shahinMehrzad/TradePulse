# Alerts

[Back to the README](../README.md)

The collector is quiet when it is well, so a night without data would only be seen by whoever opens the console. It can send a
message to **Telegram** and/or **email** instead. A channel is on only when its settings are complete; with none, alerts are off.

## What is announced

| When | Message |
| --- | --- |
| The collector starts | `Collector started`. Proves the channels work (a wrong token shows now, not at the first real problem) and shows a restart nobody asked for. |
| A health check changes (`database`, `candle-freshness`, a socket, ...) and stays changed | `⚠️ candle-freshness: degraded - 2 series behind`, `🔴 database: unhealthy - ...` |
| It is back | `✅ database: healthy again` |
| A gap appears that the exchange has no candles for | `New gap in binance_futures_btcusdt_1h: 1 gap(s), 4 candle(s) missing, from ...` |

- **Not on the first look.** A new series has its old holes found at the first repair; that is not news. Only a later repair that finds *more* is.
- **Not on a flicker.** A change is announced when the same status is seen in two health reports in a row (one every 30 s,
  `Alerts:ConfirmationsToAlert`). Each check is judged on its own, and the checks that changed together share one message.
- **Not in a flood.** At most `Alerts:MaxPerHour` (20) messages leave the collector an hour; the rest are counted and announced
  once ("Alerts were held back"). A crash loop therefore cannot flood a chat with "Collector started".
- **Every channel gets every alert**, side by side: when Telegram is slow or down the mail is not held up, and the other way round.
  A channel that fails is logged and the rest still get the message. Alerts leave from their own task and queue, so nothing here can slow the collection.

## Where the settings go

**Never in `appsettings.json` and never in the database.** They are given once, when the collector is put on a machine:

| Where the collector runs | How |
| --- | --- |
| Your machine | User Secrets, stored in your Windows profile and outside the repository: `dotnet user-secrets set "Alerts:Telegram:BotToken" "..." --project src/BackgroundWorker` |
| Docker stack | `deploy/.env` (git-ignored), see [deploy/README.md](../deploy/README.md) |
| A server | Environment variables, e.g. `Alerts__Telegram__BotToken` (two underscores for each `:`) |

The `UserSecretsId` in `BackgroundWorker.csproj` is only the name of the folder those secrets are read from; it is not a secret and belongs in the repository.

| Setting | Meaning |
| --- | --- |
| `Alerts:InstanceName` | Shown in front of every message, so several collectors can be told apart (default: the machine name). |
| `Alerts:Telegram:BotToken`, `ChatId` | The bot and the chat it writes to. Both needed. |
| `Alerts:Email:Host`, `Port`, `UseStartTls`, `UserName`, `Password`, `From`, `To` | `Host`, `From` and `To` are needed. `To` may hold several addresses separated by `,` or `;`. |
| `Alerts:ConfirmationsToAlert`, `Alerts:MaxPerHour` | See above. Defaults 2 and 20; not in any file. |

The collector logs at start which channels it found (`Alerts go to: Telegram, Email.` or `No alert channel is configured; alerts are off.`)
and `Alert sent to Telegram: Collector started` for every message that was accepted. The bot token is part of the Telegram address, so that
client logs nothing about its requests and no error ever repeats the address.

## Telegram, step by step

1. In Telegram write to **@BotFather**: `/newbot`, choose a name. It answers with the **token** (`123456789:AA...`).
2. Open your bot and press **Start** (or send any text). A bot cannot write to someone who has not written to it first.
3. Find the **chat id**: open `https://api.telegram.org/bot<token>/getUpdates` in a browser, with the token directly after `bot` (no `<` `>`, no spaces),
   and read `"chat":{"id":...}`. `404 Not Found` means the address is wrong, not the token; `401` means the token is wrong; `"result":[]` means nobody wrote to the bot yet.
4. Give both to the collector and restart it. The chat gets `Collector started`:
   ```bash
   dotnet user-secrets set "Alerts:Telegram:BotToken" "<token>"   --project src/BackgroundWorker
   dotnet user-secrets set "Alerts:Telegram:ChatId"   "<chat id>" --project src/BackgroundWorker
   ```
5. The token was in your clipboard, maybe in a chat: if it was ever shown anywhere, send `/revoke` to BotFather, take the new one and set that.

### Several people: use a group

One `ChatId` serves everybody, and people come and go inside Telegram without touching the collector.

1. Create a group, add the people and the bot.
2. Write `/start@<your_bot_name>` in the group (with the default privacy setting a bot only sees commands and mentions).
3. Read the group's id from `getUpdates`: a **negative** number, usually `-100...`. Set it as `ChatId` (the command above takes negative values as they are).
4. If sending ever fails with *"group chat was upgraded to a supergroup chat"*, the group got a new id: read it again and set it. The collector log shows Telegram's own reason.

What the log says when sending fails: `Telegram answered 401: Unauthorized` (token), `400: Bad Request: chat not found` (chat id, or nobody pressed Start),
`Telegram could not be reached (...)` (network).

## Email, when you want it

The code is ready; give it the settings and restart, and the mail is sent next to Telegram (or alone, if you remove the Telegram ones), so a
Telegram outage does not leave you without an alert. The first mail is `Collector started`.

```bash
dotnet user-secrets set "Alerts:Email:Host"     "smtp.example.com" --project src/BackgroundWorker
dotnet user-secrets set "Alerts:Email:UserName" "<user>"           --project src/BackgroundWorker
dotnet user-secrets set "Alerts:Email:Password" "<password>"       --project src/BackgroundWorker
dotnet user-secrets set "Alerts:Email:From"     "tradepulse@example.com" --project src/BackgroundWorker
dotnet user-secrets set "Alerts:Email:To"       "me@example.com, friend@example.com" --project src/BackgroundWorker
```

The mail is sent with .NET's `SmtpClient`: **port 587 with STARTTLS** (the default) or plain, with a user name and password. Implicit TLS (465) and OAuth2
are not supported. Gmail with an app password, Zoho, Fastmail and the SMTP side of transactional services (SES, Mailgun, SendGrid, Resend) work; a provider
that only accepts OAuth2 or has switched off password sign-in does not. For a server a transactional service is the safer choice. If one day it is needed,
`SmtpEmailTransport` is the only class to replace (for example with MailKit), because everything else talks to `IEmailTransport`.
