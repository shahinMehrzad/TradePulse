# Candle cache

[Back to the README](../README.md)

`CachedCandleReader` decorates `ICandleReader` with .NET's `HybridCache`: entries live in process memory (L1) and, if `ConnectionStrings:Redis` is set, also in Redis (L2) so several API instances share them. Concurrent identical requests share one database call, and errors are never cached. Answers that reach the newest candle expire after `CandleCache:LatestTtl` (30 s); answers that end more than two candles ago expire after `HistoricalTtl` (1 h, not forever, because Binance sometimes revises old candles). Set `CandleCache:Enabled` to `false` to turn it off.

Measured (BenchmarkDotNet, Postgres and Redis in Docker on the same machine, Ryzen 7 8840HS; warm cache, mean):

| Request | No cache | Redis only (L2) | Memory (L1) |
|---|---|---|---|
| latest 78 x 4h (Ichimoku lookback) | 959 us | 821 us | 0.30 us |
| latest 1 x 15m | 809 us | 565 us | 0.31 us |
| page of 500 x 15m | 1,575 us | 2,005 us | 0.33 us |

Takeaways: Postgres already answers in about 1 ms, so a Redis hit saves little (a network round trip either way) and a large page is even slower from Redis because of the JSON copy. The gain comes from the in-memory level; Redis only adds sharing between instances. Redis is opt-in: if it is configured and goes down, requests still succeed but every cache miss waits for the Redis timeout, so use short ones, e.g. `localhost:6379,abortConnect=false,connectTimeout=500,syncTimeout=500,asyncTimeout=500`.
