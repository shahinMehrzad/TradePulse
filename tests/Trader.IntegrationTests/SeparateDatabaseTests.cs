using System.Net;
using Dapper;
using Npgsql;

namespace Trader.IntegrationTests;

/// <summary>
/// The key vault has a database of its own and only the audit log and a copy of the enabled symbols go to the main one. Here the two
/// are really two databases. What decides trading (keys, orders, limits, which symbols are enabled) must never depend on the main one.
/// </summary>
public class SeparateDatabaseTests(TraderFixture trader) : IClassFixture<TraderFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<string>> CopyOfEnabledSymbolsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return (await connection.QueryAsync<string>(new CommandDefinition("SELECT symbol FROM config.traded_symbols ORDER BY symbol", cancellationToken: Ct))).ToList();
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: Ct));
    }

    [Fact]
    public async Task EnabledSymbolsAreKeptInTheVaultAndCopiedToTheMainDatabase()
    {
        var main = await trader.NewMainDatabaseAsync();
        var client = trader.ClientWith(("ConnectionStrings:Main", main));
        var admin = trader.Administrator("separate");

        Assert.Equal(HttpStatusCode.NoContent, (await trader.SendAsync(HttpMethod.Post, "/trading/symbols/ETHUSDT/enable", admin, client: client)).StatusCode);
        Assert.Contains("ETHUSDT", await CopyOfEnabledSymbolsAsync(main));
        Assert.Equal(1, await CountAsync(trader.ConnectionString, "SELECT count(*) FROM vault.trading_symbols WHERE symbol = 'ETHUSDT' AND enabled"));

        Assert.Equal(HttpStatusCode.NoContent, (await trader.SendAsync(HttpMethod.Post, "/trading/symbols/ETHUSDT/disable", admin, elevated: false, client: client)).StatusCode);
        Assert.DoesNotContain("ETHUSDT", await CopyOfEnabledSymbolsAsync(main));
    }

    [Fact]
    public async Task TheMainDatabaseGetsTheAuditEntryAndNoSecretTables()
    {
        var main = await trader.NewMainDatabaseAsync();
        var client = trader.ClientWith(("ConnectionStrings:Main", main));
        var admin = trader.Administrator("audited-apart");

        await trader.SendAsync(HttpMethod.Post, "/keys", admin, new { Label = "apart", Environment = "Demo" }, client: client);

        Assert.Equal(1, await CountAsync(main, """SELECT count(*) FROM config.audit_log WHERE "UserName" = 'audited-apart'"""));
        Assert.Equal(0, await CountAsync(main, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'vault'"));
        Assert.True(await CountAsync(trader.ConnectionString, "SELECT count(*) FROM vault.exchange_keys WHERE label = 'apart'") >= 1);
    }

    [Fact]
    public async Task TradingChoicesStillWorkWhenTheMainDatabaseCannotBeReached()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(trader.ConnectionString) { Database = $"does_not_exist_{Guid.NewGuid():N}", Timeout = 3 }.ConnectionString;
        var client = trader.ClientWith(("ConnectionStrings:Main", unreachable));
        var admin = trader.Administrator("main-down");

        var enabled = await trader.SendAsync(HttpMethod.Post, "/trading/symbols/BTCUSDT/enable", admin, client: client);
        var halted = await trader.SendAsync(HttpMethod.Post, "/trading/halt", admin, elevated: false, client: client);

        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, halted.StatusCode);
        Assert.Equal(1, await CountAsync(trader.ConnectionString, "SELECT count(*) FROM vault.trading_symbols WHERE symbol = 'BTCUSDT' AND enabled"));
    }

    [Fact]
    public async Task AMainDatabaseThatWasDownCatchesUpWhenTheTraderStarts()
    {
        var admin = trader.Administrator("catch-up");
        Assert.Equal(HttpStatusCode.NoContent, (await trader.SendAsync(HttpMethod.Post, "/trading/symbols/BTCUSDT/enable", admin)).StatusCode);

        var main = await trader.NewMainDatabaseAsync();
        using var client = trader.ClientWith(("ConnectionStrings:Main", main)); // starts a Trader, which publishes at start

        for (var attempt = 0; attempt < 50 && (await CopyOfEnabledSymbolsAsync(main)).Count == 0; attempt++)
            await Task.Delay(100, Ct);

        Assert.Contains("BTCUSDT", await CopyOfEnabledSymbolsAsync(main));
    }
}
