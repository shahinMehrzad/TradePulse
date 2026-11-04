# Tests, benchmarks and CI

[Back to the README](../README.md)

- **Unit tests** (xunit v3): value objects, command handling, rate limiter and health checks (with `FakeTimeProvider`), scheduling, the real health endpoint on Kestrel, table definitions and **contract tests** that fail if an enum value, table name or `appsettings.json` entry drifts from what other services expect.
- **Integration tests** (Testcontainers, real Postgres, needs Docker): the repositories' SQL, idempotent inserts, rollback, gap detection and the read queries.
- **Database role tests** (`DatabaseRolesTests`, real Postgres): the file the deploy runs (`deploy/main-db-roles.sql`) is applied, then the collector's own repositories connect as `tp_collector`, the API's migrations as `tp_api` and the trader's statements as `tp_trader`. Every other test connects as the superuser and would never see a missing grant; these show it, and that each role is refused what it must not do. On a server, `deploy/check-db-roles.sh` does the same check on the running stack.
- **API integration tests** (`WebApi.IntegrationTests`, the real host on a throwaway Postgres): login, bearer validation and every rejection case (no token, wrong key, issuer or audience, expired, tampered, `alg: none`, wrong algorithm), and the startup refusal without a strong secret.
- **Benchmarks** (BenchmarkDotNet, `benchmarks/TradePulse.Benchmarks`, needs Docker): read latency of the candle query layer against a seeded Postgres (300k 15m + 18.7k 4h candles), with and without the cache. Run with `dotnet run -c Release --project benchmarks/TradePulse.Benchmarks -- --filter '*'`. Results are in [Candle cache](candle-cache.md).
- **CI:** GitHub Actions builds and tests every push and pull request.
