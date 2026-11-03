# WebApi reference

[Back to the README](../README.md)

## Candle read API

Read-only access to the stored closed candles; the route names the series and the table name is derived from the parsed enums only. **Requires a bearer token** (`POST /api/auth/login`, then `Authorization: Bearer <accessToken>`).

| Request | Returns |
|---|---|
| `GET /api/candles/binance/futures/btcusdt/4h?from=&to=&limit=` | Candles in a range, oldest first, one page (default 500, max 1000). Follow `nextFrom` for the next page. |
| `GET /api/candles/binance/futures/btcusdt/4h/latest?limit=78&to=` | The newest N candles up to an optional time, oldest first: the lookback window an indicator such as Ichimoku needs. |

Times are ISO 8601 UTC (offset-less input is read as UTC). Bad input is a 400 with the offending parameter, a series the collector never created is a 404. The endpoints talk to `ICandleReader`, so the cache is a decorator behind it (see [Candle cache](candle-cache.md)).

## Dashboard

Admin only (bearer token with the admin role). Built from the stored candles for the panel's home page.

| Request | Returns |
|---|---|
| `GET /api/admin/dashboard/series?market=futures` | One card per exchange market symbol the collector gathers (`market` is optional). |
| `GET /api/admin/dashboard/series/binance/futures/btcusdt?timeframe=4h&candles=80` | The same card for one time frame, plus `related`: the funding rate, open interest and liquidations that are collected. Futures only. |

A card has the collected `timeframes`, the shortest as `defaultTimeframe`, and `current`: last close, the change, high, low and volume, a `sparkline` of closes, and `lagSeconds` since the newest candle closed. Everything in `current` describes the same newest candles (24 by default, `candles=2..500` on the second request), from `windowStart` (open of the first) to `windowEnd` (close of the last), so a card's colour always agrees with the line it draws.

`status` is `ok`, `late` (a whole candle is overdue) or `empty` (no table or no rows yet; the other fields are null). The second request answers `404` when the series or that time frame is not collected and `400` for an unknown name.

## Admin lists (OData) and the audit log

Admin only. Two lists take OData query options, and answer `{ "count": 13, "value": [...] }`, where `count` is the number of rows that matched `$filter` before paging:

| Request | Returns |
|---|---|
| `GET /api/admin/configurations/candle-worker/odata` | The candle series the collector gathers, with the names of exchange, market, symbol and time frame next to their ids. |
| `GET /api/admin/audit-log` | Who changed which collector setting: `occurredAt`, `userName`, `action` (`Added`, `Updated`, `Removed`), `entityType` (`CandleSeries`, `SymbolData`, `ShockWatch`, `Symbol`, `SymbolListing`), `entityId`, `summary` and `details` (for an update, what changed: `start date 2020-01-01 -> 2021-06-01`). |
| `GET /api/admin/shock-watch` | The symbols the live watch follows ([docs/research.md](research.md)): `id`, `symbolListingId`, exchange, market, symbol, `exchangeSymbol`, `reason` (`Manual`: added by hand to study it; `Trading`: enabled for trading, so watched on its own), `isEnabled` (the watch of this symbol is on), `isTradedNow` (enabled for trading right now), `hasMinuteCandles` (its 1 minute candles are collected: the watch starts from them) and `createDate`. A symbol watched only because it is traded has **`id` 0**: there is no row to change; use `traded/{symbolListingId}/off`. |
| `POST /api/admin/shock-watch` | Puts a symbol on the list by the admin's choice, switched on: `{ "symbolListingId": 1 }`. `400` when the listing is unknown or its 1 minute candles are not collected, `409` when it is on the list already. Answers `201 { id }`. |
| `POST /api/admin/shock-watch/traded/{symbolListingId}/off` | Turns off the watch of a symbol enabled for trading, on the admin's own responsibility. Remembered as a row (`Trading`, off), so it stays unwatched until switched on. Answers `{ id }` of that row. `400` when the symbol is not enabled for trading. |
| `PUT /api/admin/shock-watch/{id}/enabled` | Switches the watch of one row: `{ "enabled": false }`. Off always works. On is `400` for a row added by hand while its 1 minute candles are not collected; on for the row of a traded symbol removes the row (the symbol is watched on its own again, even without candles: the watch says so by message). `404` if the row does not exist. `204`. |
| `DELETE /api/admin/shock-watch/{id}` | Takes a symbol added by hand off the list (`204`); `404` if it does not exist; `400` for the row of a traded symbol (switch it on instead). The watch does not stop the collection, and the collection does not start the watch: they are separate lists. |

Only `$filter`, `$orderby`, `$top`, `$skip` and `$count` are accepted; `$select`, `$expand` and `$apply` are a `400`, like a malformed query or a `$top` above 100. Without `$top` a request gets 20 rows, and the key is always the last sort so pages never overlap. Dates in a filter are UTC (`occurredAt ge 2026-10-01T00:00:00Z`), like every stored time. Example: `?$filter=action eq 'Removed' and contains(tolower(userName),'shahin')&$orderby=occurredAt desc&$top=25`.

The audit log is written by the same API calls that change the settings (add, change, remove of a candle series or of a symbol's funding rate and liquidations), with the user name from the access token. An update that changes nothing is not logged. It is written after the change is saved; if writing it fails, the failure is logged by the API and the change itself stands.

### Symbols and where they are listed

Admin only. A series or a funding rate / liquidation setting names a **symbol listing**: one symbol (`BTC/USDT`) on one exchange market, under the name that exchange uses for it (`BTCUSDT`). Both `add-config`/`update-config` and `add-symbol-data`/`update-symbol-data` take a `symbolListingId` instead of exchange, market and symbol, so a symbol that is not listed on a market cannot be chosen for it. `GET .../candle-worker/odata` and `GET .../symbol-data` still show the exchange, market and symbol ids next to it.

| Request | Does |
|---|---|
| `GET /api/admin/symbols?keyword=&pageNumber=1&pageSize=10` | One page of the symbols by name (`pageSize` 1-100): `items` with `id`, `name`, `description` and the `markets` each is listed on (`listingId`, `exchange`, `market`, `exchangeSymbol`), plus `pageNumber`, `pageSize`, `totalItems`, `totalPages`. The keyword (any case) matches the name, the description or an exchange's name for the symbol (`btcusdt` finds `BTC/USDT`). |
| `GET /api/admin/symbols/{id}` | One symbol with every market it is listed on: `listingId`, exchange, market, `exchangeSymbol`, and `seriesCount` / `settingsCount` (how many candle series and funding rate / liquidation settings use that listing; a listing that is used cannot be removed). `404` if it does not exist. |
| `PUT /api/admin/symbols` | Changes the description: `{ "id": 1, "description": "..." }` (at most 100 characters, empty clears it). The name is never changed. `404` if it does not exist. |
| `POST /api/admin/symbols` | Adds a symbol: `{ "name": "BTC/USDT", "description": "..." }`. Upper-cased, `BASE/QUOTE`, letters and digits only; `409` if it exists. |
| `DELETE /api/admin/symbols/{id}` | Removes it; `409` while it is listed anywhere. |
| `GET /api/admin/symbols/listings` | Every listing: `id`, exchange, market, symbol and `exchangeSymbol`. |
| `POST /api/admin/symbols/listings` | Lists a symbol: `{ exchangeId, marketId, symbolId, exchangeSymbol }`. The name must be usable in a table name, must not share its table names with another listing of that exchange market (`409`), and the exchange (`exchangeInfo`) must list it as trading (`400` if not, `503` and nothing saved when the exchange cannot be asked). Stored as the exchange spells it. |
| `DELETE /api/admin/symbols/listings/{id}` | Removes a listing; `409` while a series or setting uses it. |

The candle and dashboard routes name the symbol by the table-name part of the exchange's name (`btcusdt`, `1000pepeusdt`; any case is accepted). A name that can never be part of a table name is a `400`; a valid name nobody collects is a `404`.

### Series that start later than their start date

| Request | Returns |
|---|---|
| `GET /api/admin/configurations/candle-worker/late-starts` | The series whose first stored candle is on a later day than their start date (the symbol was listed later): `id`, `startDate`, `firstCandle`. Read from the candle tables, so it works while the collector is down. |
| `POST /api/admin/configurations/candle-worker/align-starts` | Moves the start date of all of them to the open time of their first candle. Answers `{ "aligned": n }`. |
| `POST /api/admin/configurations/candle-worker/{id}/align-start` | The same for one series (`400` if it does not start later, `404` if it does not exist). |

Nothing is ever moved unless one of the `POST`s is called; each change is written to the audit log like an edit.

## Collector health

Admin only. `GET /api/admin/collector/health` relays the running collector's own `/health` (its components, each `Healthy`, `Degraded` or `Unhealthy`, with their details). It always answers `200`; `state` says whether the collector could be asked: `answered` (then `status` and `checks` are filled), `notConfigured` (`Collector:HealthUrl` is empty) or `unreachable` (down, too slow, or not answering with a health report). `Collector:HealthUrl` is the collector's `Health:Url` plus `/health` (default `http://localhost:5080/health`), and is the only place the API takes the address from. The wait is 5 seconds at most and the answer is cut off at 256 KB; why a collector was unreachable goes to the API log, never into the response.

## Behind a proxy

In the Docker stack the API is called by the panel and the panel by Caddy, so every request arrives from a private address. Both programs use `UseForwardedHeaders` and believe `X-Forwarded-For`/`-Proto` **only from a private network** (127/8, 10/8, 172.16/12, 192.168/16, ::1, fc00::/7); from a public address the headers are ignored, so nobody can choose the address the login rate limit counts. The panel adds the visitor's address when it calls the login endpoints, so `RateLimiting:LoginPermitsPerMinute` is a budget per visitor, not one for the whole panel. The refresh-token audit rows store that address too (the `X-Forwarded-For` header is never read directly any more).

`DataProtection:KeysPath` (set to `/keys` in the containers, on a volume) keeps the keys of data protection across restarts.

## Authentication

A completed sign-in (see below) issues a JWT; the candle endpoints validate its signature (ES256 only: ECDSA on the P-256 curve; a token that names any other algorithm, HMAC included, is refused), issuer, audience and lifetime (30 s clock skew) and answer 401 otherwise. **An access token lives 5 minutes** (`JwtConfigurations:DurationInMinutes`; the panel renews it by itself with the refresh token), and it can be refused earlier: for every request with a valid token the API checks that the user it was made for still exists, is not disabled, is not waiting to set a new password or two-factor up (an administrator reset them) and still has every role the token carries. So disabling a user, resetting their password or two-factor, or removing a role ends their open sessions at once (`TokenRevocation:CacheSeconds`, 10 by default, is the longest what is known about a user is reused; 0 asks the database every time). The signing key, the seeded admin credentials and the connection strings are **in no committed file**, `appsettings.Development.json` included. On a developer machine they are user secrets (`dotnet user-secrets set "JwtConfigurations:PrivateKey" ... --project src/WebApi`, see the README); anywhere else supply `JwtConfigurations__PrivateKey` (base64 PKCS#8, made by `deploy/make-jwt-keys.sh`), `ConnectionStrings__DefaultConnection` and `ConnectionStrings__AuthConnection` and, to seed an admin, `AdminConfigurations__Email` and `AdminConfigurations__Password` as environment variables. The host refuses to start without a valid private key instead of running with forgeable tokens. Only the API holds the private key; the key vault gets `JwtConfigurations__PublicKey` and so can check a token but never make one (it refuses to start if it is given the private key). Never run a public deployment with `ASPNETCORE_ENVIRONMENT=Development`.

### Signing in: password, change, two-factor

Every account has to change a password somebody else knew and has to use two-factor authentication (TOTP: Google Authenticator, Microsoft Authenticator, Authy, 1Password, ...). So no call returns tokens for a password alone. Every step answers with `stage`:

| `stage` | Meaning | Next call |
|---|---|---|
| `SetupRequired` | `mustChangePassword` and/or `needsTwoFactorSetup` | `setup/password`, then `setup/two-factor/begin` and `confirm` |
| `TwoFactorRequired` | password right, two-factor is on | `login/two-factor` |
| `Authenticated` | `accessToken`, `refreshToken`, `expiresAt` are set | |

1. `POST /api/auth/login` `{ email | username, password }` returns `SetupRequired` or `TwoFactorRequired` with a `challengeToken` (30 minutes: time to choose a password and install an authenticator app). The challenge token is a JWT for another audience: it is **not** an access token and every other endpoint rejects it. The state of the account is always re-read from the database, never taken from the token.
2. `POST /api/auth/setup/password` `{ challengeToken, currentPassword, newPassword }` replaces the temporary password (at least 12 characters, upper and lower case, a digit and a symbol; it must differ from the old one) and ends the sessions the old one opened. It answers with the next step.
3. `POST /api/auth/setup/two-factor/begin` `{ challengeToken }` returns `sharedKey` and `authenticatorUri` (the `otpauth://` address the panel draws as a QR code). It is refused until the password is changed.
4. `POST /api/auth/setup/two-factor/confirm` `{ challengeToken, code }` turns two-factor on when the code is right and signs the user in (`Authenticated`, plus **ten one-time `recoveryCodes`, shown only in this answer**).
5. `POST /api/auth/login/two-factor` `{ challengeToken, code, isRecoveryCode }` finishes a normal sign-in. A recovery code works once.

**Wrong guesses are locked out for a while, never for good.** Five wrong passwords for an account from one address lock *that address* out of *that account* (nobody else: the owner signs in from their own address as always), for 5 minutes; each round after that is longer (30 minutes, 2, 6, then 24 hours) until a right password starts the address over, and a round nobody added to for a week is forgotten. Thirty wrong passwords for one account within an hour from many addresses close the account for 15 minutes, for everybody. Five wrong two-factor codes after the right password (that is somebody who has the password) lock the whole account: 15 minutes, then 1 hour, 6 hours and 24 hours. A locked address or account gets the answer a wrong password gets, so nothing tells a stranger that a user exists. The numbers are `LoginThrottlePolicy`; no setting changes them. Another administrator lifts a lock with **Unlock** on the Users page (`POST /api/admin/users/{id}/unlock`, needs a fresh code), which forgets every count of that user; resetting the user's password or two-factor does the same, and so does `reset-access` on the server. The second wrong code in a row, every lock after wrong codes, and every lock of an address from the second round on are sent as a Telegram message to a bot of their own (`SecurityAlerts:Telegram:*`, see [alerts.md](alerts.md)), and every lock is in the audit log. All these endpoints share the login rate limit. A seeded or admin-created account starts with `MustChangePassword`, and so does every account that existed when this was introduced. A disabled user, or one whose password or two-factor was reset, gets no new session from an old refresh token.

### Users

Admin only, under `/api/admin/users`: `GET` lists the accounts (state only, never a secret); `POST { userName, email }` creates an administrator and returns their generated one-time password, **once**; `POST {id}/reset-password` returns a new one-time password; `POST {id}/reset-two-factor` makes the user enrol again; `POST {id}/disable` and `POST {id}/enable`. Resets and disabling end all of the user's sessions. Nobody can reset, disable or reset the two-factor of their own account (`409`), and the last active administrator cannot be disabled. Every change is in the audit log (entity `User`, with `entityId` 0 and the user name in the summary).

### Sign-in codes by email (optional)

A second way to pass the code step, next to the authenticator app, for users who turn it on. It needs mail settings on the API: `Email:Host`, `Port` (587), `UseStartTls` (true), `UserName`, `Password` and `From`, only from configuration (user secrets, `Email__Host` environment variables; the deploy stack passes the same `SMTP_*` values the collector's alerts use). Without `Host` and `From` the feature is not offered anywhere (`emailAvailable` is false and the endpoints answer `409`).

- A user turns it on in *My account*: `POST /api/account/email-codes/send` mails a test code to the address of the account, `POST /api/account/email-codes/enable` `{ code }` turns it on when the code is right and marks the address confirmed. Only users who already have two-factor on can; the first enrolment is always the authenticator app. `POST /api/account/email-codes/disable` `{ password }` turns it off.
- At sign-in the code step answers `emailCodeAvailable` (the user turned it on and the server can send). `POST /api/auth/login/two-factor/email` `{ challengeToken }` mails a code (`204`); `login/two-factor` then takes `{ method: "Email", code }` (`method` is `Authenticator` by default, `Recovery`, or `Email`).
- At least a minute between two codes and five an hour per user (the test code and the sign-in codes are counted apart), over and above the login rate limit; a wrong code counts toward the account's lock on wrong codes like any other. The codes come from ASP.NET Identity's email token provider: six digits, valid for a few minutes and, as with any time-based code, usable more than once in that window. The mail never appears in a log, only that the server refused it.
- Email as a second factor is only as strong as the mailbox: a stolen mailbox plus the password is enough. That is why it is opt-in per user and the authenticator app stays the way to enrol.

### Confirming sensitive actions (step-up)

Being signed in is not enough for what can hurt. These actions need a second-factor code entered a moment ago, on top of the access token: every change under `/api/admin/users`, and, when they exist, the exchange keys and trading. `POST /api/account/step-up` `{ code, method }` (`method` is `Authenticator`, the default, or `Email`; a recovery code is refused, it is for getting in when the phone is lost) returns `{ token, expiresAt }`, an **elevation token**. The caller sends it in the `X-Elevation` header with the action.

- It is good for ten minutes, names one user (another user's token is refused), is signed for an audience of its own (it is not an access token and an access token is not an elevation) and **a refresh does not renew it**: after ten minutes, or in a stolen session without the phone, the next sensitive action asks again.
- Without it the action answers `403` with `"code": "step_up_required"` in the problem details, so a client can tell "ask for a code" from "you lack the role". Reading (`GET`) does not need it.
- `POST /api/account/step-up/email` mails a code to a user who turned email codes on (`409` otherwise). Wrong codes are counted and lock like any other wrong code (see above) and the endpoints share the login rate limit.
- Put `[RequireElevation]` on a new sensitive action; that is all it takes.

### My account

Any signed-in user (not only an administrator), always about the user in the token: `GET /api/account` (user name, email, roles, whether two-factor is on, how many recovery codes are left; never a secret); `POST /api/account/change-password` `{ currentPassword, newPassword }` signs the user out everywhere except the device named in `X-Device-Id`; `POST /api/account/recovery-codes` `{ password }` makes ten new recovery codes (shown only in this answer) and the old ones stop working. A wrong password on either counts towards the lockout, and both share the login rate limit.

### Getting back in from the server

When the only administrator has lost the phone and the recovery codes, nobody in the panel can help. Somebody with a shell on the server runs `dotnet WebApi.dll reset-access <user name or email>` (in the deploy stack: `docker compose exec api dotnet WebApi.dll reset-access admin`). It enables the account, lifts a lockout, forgets two-factor and the recovery codes, ends all sessions, prints a new one-time password on the console (never in a log) and exits; the audit log records it as done by the `server console`. The user signs in with it, chooses a new password and sets up two-factor again. Exit code 1 means there is no such user.

`POST /api/auth/refresh-token` with `{ "refreshToken": "..." }` (and the same `X-Device-Id` header used at login) returns a new access token and a new refresh token. It needs no access token, since that is usually expired by then. Refresh tokens are single-use (rotated), bound to the device they were issued to, expire after `RefreshTokenDurationInDays`, and only their SHA-256 hash is stored. Concurrent use of one token succeeds exactly once. **A token that was already renewed and is shown again** (more than ten seconds later: a copy of the session exists) ends every session of the device it was issued to, whichever device name the caller gives, is written to the audit log (as `system`) and sends a security alert (see [alerts.md](alerts.md)); within ten seconds it is only refused (an answer that was lost and asked for again). A token that was only signed out or expired is a plain refusal, never an alarm. **`POST /api/account/logout`** (signed in, with the `X-Device-Id` of the session) ends the refresh tokens of that device at once and leaves the user's other devices alone; the access token already issued runs out by itself within minutes. The panel calls it when you sign out.

**A two-factor code works once.** The codes of the authenticator app and of the email are valid for a minute or two, and Identity lets one be used again in that time; here the same code of the same user is refused the second time (`401` at sign-in, `400` at the step-up, "That code was already used"), for ten minutes. A repeated code is neither a success nor a wrong guess: it does not lock the account or send a message. The practical meaning: the code you signed in with cannot confirm a page the next second; wait for the next one, it changes every 30 seconds. Recovery codes were always one-time.

**Anything that does not say who may use it needs a signed-in user** (a fallback policy), so a new endpoint cannot go live open by forgetting an attribute; a visitor who is not signed in gets `401` for an address that does not exist as well as for one that does. The endpoints open to everybody are the sign-in steps, `refresh-token`, the lookups and (in Development) the OpenAPI description; a test pins that list. **The answer to a password takes the same time whatever it is about**: for a user that does not exist, and for an address that is locked out, a stand-in hash is compared, so the time alone does not say which names exist or that a lock is in force.
