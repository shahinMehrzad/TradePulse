# ctrader-check

For the day the cTrader application is approved: get the tokens without a browser plugin, and look at what the broker really does before
the collector depends on it (see [Pepperstone in docs/collector.md](../../docs/collector.md#pepperstone-ctrader)). It never places an order.

```bash
# 1. The owner of the account allows the application. Needs the Client ID and secret of the application at connect.spotware.com
#    (CTRADER_CLIENT_ID and CTRADER_CLIENT_SECRET in the environment, or typed when asked; the secret is not shown).
#    The redirect address must be one registered for the application (default http://localhost:5000/callback).
dotnet run --project tools/ctrader-check -- token --env demo

# 2. What the broker really does, with the settings that step 1 saved.
dotnet run --project tools/ctrader-check -- check --symbol EURUSD --days 14

# Every month or so (the access token lives about 30 days): a new pair of tokens, no browser.
dotnet run --project tools/ctrader-check -- refresh --env demo
```

- **`token`** opens nothing itself: it prints the address to open in a browser where you are signed in to cTrader, listens on the redirect address
  for the answer, exchanges the code (valid for one minute) for the tokens and asks cTrader which accounts they open. The **read only**
  token (scope `accounts`, the default) goes to the user secrets of the collector and the API, with the application's id and secret and the account
  id; the refresh token goes to this tool's own user secrets. Nothing is written to a file in the repository, and no secret is printed.
  A token that can trade (`--scope trading`) is never written to the collector's or the API's secrets: it is for the trading service.
  `--print` shows the access token once, for `deploy/.env` on the server.
- **`refresh`** makes a new pair of tokens from the stored refresh token. cTrader invalidates the old pair when it does, so the new refresh
  token is stored first.
- **`check`** prints, for one symbol: that the connection signs in, the symbol's digits, time zone and weekly sessions, the last days of 1 hour candles
  through the collector's own gateway, whether the trading calendar explains every hole in them, whether each candle period (M1 to D1) starts on
  the grid the collector assumes (multiples of the period since the epoch; 4h, 12h and 1d are left out of the collector until this says yes), how long a
  range one request may ask for (against what the adapter assumes), and how far back tick data answers.

The user secrets files are the ones of `dotnet user-secrets` (`%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json` on Windows,
`~/.microsoft/usersecrets/<id>/secrets.json` elsewhere), read and written directly so that a secret never goes through a command line.
Do not paste the output of `--print` anywhere.
