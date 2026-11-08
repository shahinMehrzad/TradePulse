# Sending signals: a guide for the sender

You have a program that makes trading signals. This page is all you need to send them to the signal service. What the service does with a signal
(paper trades, Telegram messages) is in [signals.md](signals.md); you do not need it to start.

## What you get from the operator

| What | Example | Note |
| --- | --- | --- |
| The secret | 64 hex characters | Used only to sign requests. Never put it in the code, a log or a message. |
| The address | `http://signals:8080/api/signals` | Inside the Docker network `tradepulse-signals`. From the server itself: `http://127.0.0.1:8090/api/signals`. There is no public address. |
| Your strategy label | `S1` | A label with no row on the service is refused (`UnknownStrategy`). |
| The symbols | `BTC/USDT`, ... | A symbol that is not listed is refused (`UnknownSymbol`). |

Your own compose file joins the network like this:

```yaml
services:
  watcher:
    networks: [signals-net]
networks:
  signals-net:
    external: true
    name: tradepulse-signals
```

The network is created when the signal service first starts. You can reach nothing else of the stack through it.

## Reading the stored candles (optional)

If the sender wants the candles that this stack collects, it reads them from the database. It does not need to ask Binance, whose request limit is per IP address and is
shared with the collector on the same server.

| What | Value |
| --- | --- |
| Network | `tradepulse-data` (`external: true` in your compose file, like `tradepulse-signals`). Only Postgres is on it, and it has no way out to the internet. |
| Host, port | `tradepulse-postgres`, `5432` |
| Database | `tradepulse` |
| User | `tp_signal_source`, with the password the operator gives you. It can only `SELECT` the candle, funding and other data tables, nothing else. |

Table names are built like `binance_futures_btcusdt_4h` (lower case: exchange, market, symbol without the slash, time frame). Times are `timestamp` columns, always UTC.

## One request

`POST /api/signals`, a JSON body, three headers.

| Header | Value |
| --- | --- |
| `X-Timestamp` | The time of **this request**, UTC, ISO 8601: `2026-11-07T08:00:02Z`. The service's clock may differ from yours by five minutes at most. |
| `X-Signature` | `HMAC-SHA256(secret, X-Timestamp + "." + body)` as lower-case hexadecimal (a leading `sha256=` is accepted). Sign exactly the bytes you send. |
| `Idempotency-Key` | The `signalId` of the body. |
| `Content-Type` | `application/json` |

**The key is the secret's text as UTF-8 bytes.** A 64-character hex secret is used as 64 characters, not decoded into 32 bytes (in .NET: `Encoding.UTF8.GetBytes(secret)`,
never `Convert.FromHexString(secret)`). The message is `X-Timestamp + "." + body`, also UTF-8.

Python:

```python
import hashlib, hmac, json, requests
from datetime import datetime, timezone

def send(url, secret, signal):
    body = json.dumps(signal, separators=(",", ":"))           # these exact bytes are signed and sent
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    sig = hmac.new(secret.encode(), f"{ts}.{body}".encode(), hashlib.sha256).hexdigest()
    return requests.post(url, data=body.encode(), timeout=10, headers={
        "X-Timestamp": ts, "X-Signature": sig,
        "Idempotency-Key": signal["signalId"], "Content-Type": "application/json"})
```

## The body

```json
{
  "signalId": "S1:BTCUSDT:4h:2026-11-07T04:00Z:long",
  "strategy": "S1",
  "exchange": "Binance",
  "market": "Futures",
  "symbol": "BTC/USDT",
  "side": "Long",
  "leverage": 5,
  "takeProfit": 68900.0,
  "stopLoss": 66800.0,
  "signalPrice": 67420.5,
  "timeframe": "4h",
  "candleOpenTime": "2026-11-07T04:00:00Z",
  "candleCloseTime": "2026-11-07T08:00:00Z",
  "validUntil": "2026-11-07T08:45:00Z",
  "maxHours": null
}
```

| Field | Rule |
| --- | --- |
| `signalId` | `strategy:SYMBOL:timeframe:candleOpenTime:side`. Symbol without the slash, upper case. The time is `yyyy-MM-ddTHH:mmZ`. Side in lower case. It is checked: a wrong id is refused (`SignalIdMismatch`). The same candle, symbol and side must always give the same id: that is what makes a retry safe. |
| `strategy` | 1 to 32 characters: letters, digits, `_`, `-`. |
| `exchange` | Optional. `Binance` when left out. |
| `market` | `Futures`. Nothing else is taken. |
| `symbol` | `BASE/QUOTE` in upper case. |
| `side` | `Long` or `Short` (names, not numbers). |
| `leverage`, `takeProfit`, `stopLoss`, `signalPrice` | Numbers above 0. The levels are **absolute prices**. A long needs `stopLoss < signalPrice < takeProfit`, a short `takeProfit < signalPrice < stopLoss`. |
| `timeframe` | `15m`, `1h`, `4h`, `1d`, ... |
| `candleOpenTime` | The open time of the **closed** candle that made the signal. A 4h signal that you send at 08:00:02 carries 04:00. |
| `candleCloseTime` | `candleOpenTime` plus the time frame (08:00). The age of the signal is counted from here. |
| `validUntil` | The last moment you still want the signal taken. After it the service refuses it and you must stop retrying. |
| `maxHours` | Optional. The trade ends at its current price after this many hours if it reached neither level. `null`: it stays open until it does. |

Prices are taken as sent: the service does not round them to a tick size (a paper trade does not need it). Round to a sensible number of digits anyway (up to 8 decimals).
**`leverage` is a ceiling, not a recommendation.** Send the highest leverage at which the stop you set still comes before the liquidation, and `1` when no leverage is safe.
The service shows it in the chat as `Max leverage: ×5 (the highest allowed, not a recommendation)`. It is accepted from 1 up to a guard the operator sets per strategy (at most 125).

Every time carries a zone (`Z`). Unknown fields are ignored. The body is at most 16 KB.

## The answers, and what to do

| Status | Meaning | What to do |
| --- | --- | --- |
| `202` | Taken, a paper trade is open. `{"signalId","tradeId"}` | Done. |
| `200` | This signal was taken before. `{"signalId","status":"duplicate"}` | Done. |
| `422` | Refused. `{"signalId","reason"}` | **Do not send it again**: the same signal gets the same answer. Log the reason. |
| `400` | Not the contract (`error` names the field), or `Idempotency-Key` is not the `signalId`. | Fix the sender. |
| `401` | No or wrong signature, or your clock is more than five minutes off. | Check the secret and the clock. Stop and alert. |
| `413` | Body over 16 KB. | Fix the sender. |
| `503` | Could not be judged now (no fresh price of the symbol, or the database is down). `Retry-After` gives seconds. | Send again after that, with a **new** timestamp and signature and the same body and key, until `validUntil`. |
| `429`, `500`, `502`, `504` | Not from the service's own logic (a proxy or a fault). | Treat like `503`: send again with a new signature and a growing delay until `validUntil`. |
| Any other `4xx` | Unknown. | Do not retry. Log it and alert. |
| Timeout or network error | You do not know if it arrived. | Send again the same way. A repeat is safe: you get `200 duplicate` if it did arrive. |

The reasons of a `422`, in the order they are checked: `StrategyDisabled`, `UnsupportedMarket`, `SignalIdMismatch`, `InconsistentCandleTimes`, `LeverageOutOfRange`,
`LevelsOnWrongSide`, `SignalFromTheFuture`, `SignalExpired`, `SignalTooOld`, `PriceBeyondLevels` (the live price is already at or past a level), `PriceMovedTooFar`,
`TooManyOpenTrades`, `UnknownStrategy`, `UnknownSymbol`.

`PriceMovedTooFar`, `SignalTooOld` and `TooManyOpenTrades` depend on limits the operator sets per strategy. Ask the operator if you meet them often.

## Rules of good behaviour

1. **Send a signal once per candle, symbol and side**, and retry only on `503`, a timeout or a network error.
2. **Never retry a `422`, `400`, `401` or `413`.** Alert on `401` and `400`: they mean a bug or a wrong secret.
3. **A new timestamp and signature on every attempt**, including retries. The same signature sent twice later is refused as stale.
4. **Keep your clock correct** (NTP). Five minutes is the limit.
5. **Rolling the secret:** the operator gives you a new one and keeps the old one working for a few days. Change it on your side when told.
6. Keep `validUntil` honest: it is how you say "after this I no longer want it". A usual value is a short time after the candle closed.

## Trying it

Ask the operator for a test secret and the address. First send one signal that should be refused (a made-up `strategy`): you must get `422 UnknownStrategy`.
That proves the address, the signature and the network all work. Then send a real signal with levels close to the price and watch the answer: `202` and a message in the chat.
