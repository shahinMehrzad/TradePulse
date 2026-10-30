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
| `GET /api/admin/audit-log` | Who changed which collector setting: `occurredAt`, `userName`, `action` (`Added`, `Updated`, `Removed`), `entityType` (`CandleSeries`, `SymbolData`), `entityId`, `summary` and `details` (for an update, what changed: `start date 2020-01-01 -> 2021-06-01`). |

Only `$filter`, `$orderby`, `$top`, `$skip` and `$count` are accepted; `$select`, `$expand` and `$apply` are a `400`, like a malformed query or a `$top` above 100. Without `$top` a request gets 20 rows, and the key is always the last sort so pages never overlap. Dates in a filter are UTC (`occurredAt ge 2026-10-01T00:00:00Z`), like every stored time. Example: `?$filter=action eq 'Removed' and contains(tolower(userName),'shahin')&$orderby=occurredAt desc&$top=25`.

The audit log is written by the same API calls that change the settings (add, change, remove of a candle series or of a symbol's funding rate and liquidations), with the user name from the access token. An update that changes nothing is not logged. It is written after the change is saved; if writing it fails, the failure is logged by the API and the change itself stands.

### Series that start later than their start date

| Request | Returns |
|---|---|
| `GET /api/admin/configurations/candle-worker/late-starts` | The series whose first stored candle is on a later day than their start date (the symbol was listed later): `id`, `startDate`, `firstCandle`. Read from the candle tables, so it works while the collector is down. |
| `POST /api/admin/configurations/candle-worker/align-starts` | Moves the start date of all of them to the open time of their first candle. Answers `{ "aligned": n }`. |
| `POST /api/admin/configurations/candle-worker/{id}/align-start` | The same for one series (`400` if it does not start later, `404` if it does not exist). |

Nothing is ever moved unless one of the `POST`s is called; each change is written to the audit log like an edit.

## Collector health

Admin only. `GET /api/admin/collector/health` relays the running collector's own `/health` (its components, each `Healthy`, `Degraded` or `Unhealthy`, with their details). It always answers `200`; `state` says whether the collector could be asked: `answered` (then `status` and `checks` are filled), `notConfigured` (`Collector:HealthUrl` is empty) or `unreachable` (down, too slow, or not answering with a health report). `Collector:HealthUrl` is the collector's `Health:Url` plus `/health` (default `http://localhost:5080/health`), and is the only place the API takes the address from. The wait is 5 seconds at most and the answer is cut off at 256 KB; why a collector was unreachable goes to the API log, never into the response.

## Authentication

A completed sign-in (see below) issues a JWT; the candle endpoints validate its signature (HMAC-SHA256 only), issuer, audience and lifetime (30 s clock skew) and answer 401 otherwise. The signing secret, the seeded admin credentials and the connection strings are **in no committed file**, `appsettings.Development.json` included. On a developer machine they are user secrets (`dotnet user-secrets set "JwtConfigurations:Secret" ... --project src/WebApi`, see the README); anywhere else supply `JwtConfigurations__Secret` (at least 32 bytes), `ConnectionStrings__DefaultConnection` and `ConnectionStrings__AuthConnection` and, to seed an admin, `AdminConfigurations__Email` and `AdminConfigurations__Password` as environment variables. The host refuses to start without a strong secret instead of running with forgeable tokens. Never run a public deployment with `ASPNETCORE_ENVIRONMENT=Development`.

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

Five wrong passwords or codes lock the account for 15 minutes. All these endpoints share the login rate limit. A seeded or admin-created account starts with `MustChangePassword`, and so does every account that existed when this was introduced. A disabled user, or one whose password or two-factor was reset, gets no new session from an old refresh token.

### Users

Admin only, under `/api/admin/users`: `GET` lists the accounts (state only, never a secret); `POST { userName, email }` creates an administrator and returns their generated one-time password, **once**; `POST {id}/reset-password` returns a new one-time password; `POST {id}/reset-two-factor` makes the user enrol again; `POST {id}/disable` and `POST {id}/enable`. Resets and disabling end all of the user's sessions. Nobody can reset, disable or reset the two-factor of their own account (`409`), and the last active administrator cannot be disabled. Every change is in the audit log (entity `User`, with `entityId` 0 and the user name in the summary).

### My account

Any signed-in user (not only an administrator), always about the user in the token: `GET /api/account` (user name, email, roles, whether two-factor is on, how many recovery codes are left; never a secret); `POST /api/account/change-password` `{ currentPassword, newPassword }` signs the user out everywhere except the device named in `X-Device-Id`; `POST /api/account/recovery-codes` `{ password }` makes ten new recovery codes (shown only in this answer) and the old ones stop working. A wrong password on either counts towards the lockout, and both share the login rate limit.

### Getting back in from the server

When the only administrator has lost the phone and the recovery codes, nobody in the panel can help. Somebody with a shell on the server runs `dotnet WebApi.dll reset-access <user name or email>` (in the deploy stack: `docker compose exec api dotnet WebApi.dll reset-access admin`). It enables the account, lifts a lockout, forgets two-factor and the recovery codes, ends all sessions, prints a new one-time password on the console (never in a log) and exits; the audit log records it as done by the `server console`. The user signs in with it, chooses a new password and sets up two-factor again. Exit code 1 means there is no such user.

`POST /api/auth/refresh-token` with `{ "refreshToken": "..." }` (and the same `X-Device-Id` header used at login) returns a new access token and a new refresh token. It needs no access token, since that is usually expired by then. Refresh tokens are single-use (rotated), bound to the device they were issued to, expire after `RefreshTokenDurationInDays`, and only their SHA-256 hash is stored. Concurrent use of one token succeeds exactly once.
