# Trading

Manual **market orders** on Binance USDS-M Futures, sent by the trading service (the `trader` program, the same one that holds the
[exchange keys](exchange-keys.md)) with the signed-in administrator's own key. The panel page is **Trading**.

Automatic signals (for example from the ML project) are not built yet; today an order is typed in the panel.

## Demo and Live

Every key is made as **Demo** (Binance's play-money environment, host `demo-fapi.binance.com`) or **Live** (real money). It is fixed for the life of the key
and shown as a badge. A Demo key skips the permission check (the Demo environment has no Wallet API and holds no money); a Live key must pass it in full.

**Real money is off.** Live keys can be stored and checked, but no order goes out with one until the owner of the server sets `TRADING_ALLOW_LIVE=true`
in `deploy/.env` and restarts the `trader` service.

## The limits (set on the server, not in the panel)

| Setting (`deploy/.env`) | Default | Meaning |
| --- | --- | --- |
| `TRADING_ALLOW_LIVE` | `false` | Live keys may trade. |
| `TRADING_MAX_ORDER_USD` | `100` | The largest single opening order (quantity x mark price, USDT). |
| `TRADING_MAX_DAILY_USD` | `500` | The most one user may open per UTC day. Orders Binance refused do not count; orders whose result is unknown do. |
| `TRADING_SYMBOL_1`, `_2` | `BTCUSDT`, `ETHUSDT` | The symbols that may be traded. |
| `TRADING_HALTED` | `false` | Stops every new position for every user. |

An order whose size cannot be checked (no price from Binance) is not sent. The daily budget is reserved in the database *before* Binance is asked, in one
statement, so two orders at the same moment cannot both fit and a crash in between cannot lose the spending.

## Halt (kill switch)

- **Halt trading now** (panel) stops the user's new positions at once, with no extra code: in an emergency nothing should be in the way.
  **Resume** asks for a fresh second-factor code.
- An order marked **only reduce** (`reduceOnly`) still goes through while halted and is not counted against the caps, so a position can always be closed.
- Halting does **not** cancel open orders or close positions at Binance; do that at Binance.
- `TRADING_HALTED=true` halts everybody and can only be lifted on the server.

## Other rules

- Placing an order needs an administrator, a key of their own in status **Active**, and a **second-factor code entered in the last 10 minutes**
  (the same step-up as for the keys). At most 10 orders a minute per user.
- Every order is written to the audit log (who, symbol, side, quantity, environment, result) and to `vault.orders`.
- Result of an order: **Placed** (Binance accepted it), **Refused** (Binance said no; nothing opened), **Unknown** (no answer; it may exist at Binance:
  check the position there; it keeps counting against the budget).

## Not done

- Orders from the ML project (RabbitMQ) and any automatic trading. It needs its own decision about who may sign orders without a human.
- Stop-loss / take-profit, limit orders, leverage and margin-mode settings (set them at Binance).
- A daily **loss** limit (needs the realised profit from Binance); today the caps are on what is opened.
- Not tried against the real Binance, Demo included. Watch the first orders.
