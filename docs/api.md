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
| `GET /api/admin/audit-log` | Who changed which collector setting: `occurredAt`, `userName`, `action` (`Added`, `Updated`, `Removed`), `entityType` (`CandleSeries`, `SymbolData`, `Symbol`, `SymbolListing`), `entityId`, `summary` and `details` (for an update, what changed: `start date 2020-01-01 -> 2021-06-01`). |

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

## Authentication

`POST /api/auth/login` issues a JWT; the candle endpoints validate its signature (HMAC-SHA256 only), issuer, audience and lifetime (30 s clock skew) and answer 401 otherwise. The signing secret, the seeded admin credentials and the connection strings are **in no committed file**, `appsettings.Development.json` included. On a developer machine they are user secrets (`dotnet user-secrets set "JwtConfigurations:Secret" ... --project src/WebApi`, see the README); anywhere else supply `JwtConfigurations__Secret` (at least 32 bytes), `ConnectionStrings__DefaultConnection` and `ConnectionStrings__AuthConnection` and, to seed an admin, `AdminConfigurations__Email` and `AdminConfigurations__Password` as environment variables. The host refuses to start without a strong secret instead of running with forgeable tokens. Never run a public deployment with `ASPNETCORE_ENVIRONMENT=Development`.

`POST /api/auth/refresh-token` with `{ "refreshToken": "..." }` (and the same `X-Device-Id` header used at login) returns a new access token and a new refresh token. It needs no access token, since that is usually expired by then. Refresh tokens are single-use (rotated), bound to the device they were issued to, expire after `RefreshTokenDurationInDays`, and only their SHA-256 hash is stored. Concurrent use of one token succeeds exactly once.
