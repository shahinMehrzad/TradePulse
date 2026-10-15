# TradePulse

[![CI](https://github.com/shahinMehrzad/TradePulse/actions/workflows/ci.yml/badge.svg)](https://github.com/shahinMehrzad/TradePulse/actions/workflows/ci.yml)

A learning project for practicing advanced .NET and software architecture on a real problem: collecting and storing cryptocurrency market data (OHLCV candles). It is a **work in progress**, built to try ideas and deepen skills, not a production system.

## What works today

- **Layered solution** (`Domain`, `Application`, `Infrastructure`, `BackgroundWorker`, `WebApi`, `WebRazor`) targeting .NET 10.
- **Domain model:** `Candle` entity with immutable value objects `Price` and `TimeRange`, both validating their invariants.
- **Application layer:** a command (`SaveCandlesCommand`) with its own handler abstraction (`ICommandHandler`), plus a table name registry that builds one PostgreSQL table per exchange, market type, symbol and timeframe.
- **Infrastructure:** PostgreSQL access with Npgsql and Dapper. Candles are written with a binary `COPY` into a temporary table and merged with `ON CONFLICT DO NOTHING`, so repeated syncs do not create duplicates.
- **Background worker:** syncs historical and new candles from Binance (Spot and Futures) using Binance.Net, with a request weight limiter, bounded parallelism, retries, and scheduling aligned to candle close times. Symbols and timeframes are configured in `src/BackgroundWorker/appsettings.json`.
- **Local infrastructure:** `docker-compose.yml` for PostgreSQL, Redis and RabbitMQ.
- **Tests and CI:** xUnit unit tests for the domain and application layers, run on every push and pull request with GitHub Actions.

## Roadmap (not implemented yet)

These are the topics this project is meant to be used for. They are goals, not current features.

- [ ] CQRS with MediatR (currently a hand-written command handler)
- [ ] Event-driven messaging with RabbitMQ (the container is in `docker-compose.yml`, but no code uses it yet)
- [ ] Caching with in-memory cache and Redis
- [ ] Real-time updates with SignalR
- [ ] Web API endpoints for reading candles, and a real UI in `WebRazor`
- [ ] Signal management on top of the candle data
- [ ] Low-allocation work with `Span<T>`/`Memory<T>`, backed by benchmarks
- [ ] Integration tests against a real PostgreSQL instance
- [ ] Dockerfiles for the application projects

## Getting started

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker.

```bash
# 1. Create the external volume used by docker-compose.yml (once)
docker volume create docker_pgdata

# 2. Start PostgreSQL (add redis and rabbitmq if you want them running)
docker compose up -d postgres

# 3. Create the database the worker connects to (once)
docker exec market_postgres psql -U postgres -c "CREATE DATABASE trading_db"

# 4. Run the Binance candle worker
dotnet run --project src/BackgroundWorker
```

The worker connects with the connection string in `src/BackgroundWorker/appsettings.json` (`trading_db`, user `postgres`, password `password`). These are local development defaults only. Change them for anything else.

## Build and test

```bash
dotnet build DotNetTradePulse.slnx
dotnet test DotNetTradePulse.slnx
```

## Project layout

```
src/
  Domain/            Entities, value objects, enums, repository contracts
  Application/       Commands, handlers, services
  Infrastructure/    PostgreSQL repository, Binance configuration
  BackgroundWorker/  Binance candle sync worker
  WebApi/            ASP.NET Core Web API (template, endpoints still to do)
  WebRazor/          Razor Pages client (template)
tests/
  UnitTests/         xUnit tests
```

## License

MIT
