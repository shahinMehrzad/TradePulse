using Dapper;
using Infrastructure.Repositories;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// The symbols enabled for trading, read from the copy the trading service publishes (<c>config.traded_symbols</c>, made by the API's
/// migration <c>TradedSymbolsCopy</c>), on a real Postgres. The table here is made the way that migration makes it: that is the contract.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TradedSymbolSourceTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> NewDatabaseAsync(bool withTradingTable)
    {
        var database = $"traded_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var create = new NpgsqlCommand($"create database {database}", admin);
        await create.ExecuteNonQueryAsync(Ct);

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
        if (withTradingTable)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.ExecuteAsync("""
                CREATE SCHEMA IF NOT EXISTS config;
                CREATE TABLE IF NOT EXISTS config.traded_symbols (
                    symbol     text PRIMARY KEY,
                    changed_at timestamp NOT NULL
                );
                """);
        }

        return connectionString;
    }

    private static async Task AddAsync(string connectionString, string symbol)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(
            "INSERT INTO config.traded_symbols (symbol, changed_at) VALUES (@symbol, now() at time zone 'utc')", new { symbol });
    }

    [Fact]
    public async Task EveryPublishedSymbolIsAnswered()
    {
        var connectionString = await NewDatabaseAsync(withTradingTable: true);
        await AddAsync(connectionString, "BTCUSDT");
        await AddAsync(connectionString, "SOLUSDT");

        var enabled = await new TradedSymbolSource(connectionString).GetEnabledAsync(Ct);

        Assert.Equal(["BTCUSDT", "SOLUSDT"], enabled.Order());
    }

    [Fact]
    public async Task TheNamesAreUpperCaseAndMatchedWithoutRegardToCase()
    {
        var connectionString = await NewDatabaseAsync(withTradingTable: true);
        await AddAsync(connectionString, "btcusdt");

        var enabled = await new TradedSymbolSource(connectionString).GetEnabledAsync(Ct);

        Assert.Equal(["BTCUSDT"], enabled);
        Assert.Contains("btcUSDT", enabled);
    }

    [Fact]
    public async Task NothingEnabledIsAnEmptySet()
    {
        var connectionString = await NewDatabaseAsync(withTradingTable: true);

        Assert.Empty(await new TradedSymbolSource(connectionString).GetEnabledAsync(Ct));
    }

    [Fact]
    public async Task WhereTheTradingServiceNeverRanThereIsNoTableAndThatMeansNoSymbols()
    {
        var connectionString = await NewDatabaseAsync(withTradingTable: false);

        Assert.Empty(await new TradedSymbolSource(connectionString).GetEnabledAsync(Ct));
    }

    [Fact]
    public async Task ADatabaseThatCannotBeReadIsAFailureNotAnEmptyAnswer()
    {
        var missing = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = $"does_not_exist_{Guid.NewGuid():N}" }.ConnectionString;

        await Assert.ThrowsAsync<PostgresException>(() => new TradedSymbolSource(missing).GetEnabledAsync(Ct));
    }
}
