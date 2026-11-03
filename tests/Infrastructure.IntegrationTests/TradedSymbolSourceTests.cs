using Dapper;
using Infrastructure.Repositories;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// The symbols enabled for trading, read from the table the trading service keeps, on a real Postgres. The table here is made the way
/// the trading service makes it (<c>TradingRepository.EnsureSchemaAsync</c>): that is the contract between the two.
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
                CREATE SCHEMA IF NOT EXISTS vault;
                CREATE TABLE IF NOT EXISTS vault.trading_symbols (
                    symbol     text PRIMARY KEY,
                    enabled    boolean NOT NULL,
                    changed_by text NOT NULL,
                    changed_at timestamp NOT NULL
                );
                """);
        }

        return connectionString;
    }

    private static async Task SetAsync(string connectionString, string symbol, bool enabled)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(
            "INSERT INTO vault.trading_symbols (symbol, enabled, changed_by, changed_at) VALUES (@symbol, @enabled, 'admin', now() at time zone 'utc')",
            new { symbol, enabled });
    }

    [Fact]
    public async Task OnlyTheSymbolsThatAreEnabledAreAnswered()
    {
        var connectionString = await NewDatabaseAsync(withTradingTable: true);
        await SetAsync(connectionString, "BTCUSDT", true);
        await SetAsync(connectionString, "ETHUSDT", false);
        await SetAsync(connectionString, "SOLUSDT", true);

        var enabled = await new TradedSymbolSource(connectionString).GetEnabledAsync(Ct);

        Assert.Equal(["BTCUSDT", "SOLUSDT"], enabled.Order());
    }

    [Fact]
    public async Task TheNamesAreUpperCaseAndMatchedWithoutRegardToCase()
    {
        var connectionString = await NewDatabaseAsync(withTradingTable: true);
        await SetAsync(connectionString, "btcusdt", true);

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
