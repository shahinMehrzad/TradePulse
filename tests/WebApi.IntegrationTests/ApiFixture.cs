using Application.Exchange;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace WebApi.IntegrationTests;

/// <summary>
/// The real WebApi host (Program.cs, real middleware, real JWT validation) on top of a throwaway Postgres.
/// The database holds the auth schema (migrated and seeded by the app itself) and one small candle table.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string CandleUrl = "/api/candles/binance/futures/btcusdt/1h/latest?limit=5";

    // What the administrator is seeded with in these tests.
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "AdminPassword123!";

    /// <summary>The key the API signs its tokens with in these tests; tests that make a token of their own use it too.</summary>
    public const string JwtSecret = "signing-key-of-the-integration-tests-of-the-api-0123456789";

    /// <summary>The exchange's symbol list the API asks when a symbol is listed; no test reaches the real Binance.</summary>
    public FakeExchangeSymbolCatalog ExchangeCatalog { get; } = new();

    public string ConnectionString => _postgres.GetConnectionString();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var repository = new CandleRepository(_postgres.GetConnectionString());
        await repository.EnsureTableExistsAsync("binance_futures_btcusdt_1h", CancellationToken.None);

        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = Enumerable.Range(0, 10).Select(i =>
            new Candle(start.AddHours(i), 1m, 4m, 0.5m, 3m, 10m, start.AddHours(i + 1).AddMilliseconds(-1), 25m, 42, 6m, 15m));
        await repository.BulkInsert("binance_futures_btcusdt_1h", candles, CancellationToken.None);

        await ListSymbolsOnSpotAsync();
    }

    /// <summary>
    /// The migration lists the symbols on Binance Futures only. Some tests need a second market, so they are listed on Spot too, in
    /// the order that gives them the ids <see cref="ListingIds"/> expects.
    /// </summary>
    private async Task ListSymbolsOnSpotAsync()
    {
        using var client = CreateClient();   // starts the host, which migrates the database
        var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = AdminEmail, Username = "", Password = AdminPassword });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<LoginResult>())!.AccessToken);

        foreach (var symbol in Enum.GetValues<SeededSymbol>())
        {
            var response = await client.PostAsJsonAsync("/api/admin/symbols/listings",
                new { exchangeId = (int)ExchangeEnum.Binance, marketId = (int)MarketTypeEnum.Spot, symbolId = (int)symbol, exchangeSymbol = symbol.ToString() });
            response.EnsureSuccessStatusCode();

            var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;
            if (id != ListingIds.Of((int)ExchangeEnum.Binance, (int)MarketTypeEnum.Spot, (int)symbol))
                throw new InvalidOperationException($"{symbol} on Spot got listing id {id}, not the one ListingIds expects.");
        }
    }

    private sealed record LoginResult(string AccessToken);
    private sealed record Created(int Id);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = _postgres.GetConnectionString();
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("ConnectionStrings:AuthConnection", connectionString);
        builder.UseSetting("ConnectionStrings:ConfigConnection", connectionString);
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("JwtConfigurations:Secret", JwtSecret);
        builder.UseSetting("AdminConfigurations:Email", AdminEmail);
        builder.UseSetting("AdminConfigurations:Password", AdminPassword);
        // No collector runs next to the tests (the default address would find a real one on a developer machine).
        builder.UseSetting("Collector:HealthUrl", "");
        // Many tests log in within a minute; the throttle itself is tested with its own low limit.
        builder.UseSetting("RateLimiting:LoginPermitsPerMinute", "1000");
        builder.ConfigureTestServices(services => services.AddSingleton<IExchangeSymbolCatalog>(ExchangeCatalog));
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
