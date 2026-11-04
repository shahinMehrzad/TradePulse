using Dapper;
using Application.Research.Watch;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Infrastructure.Repositories.TimeSeries;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// The roles of <c>deploy/main-db-roles.sql</c> on a real Postgres, with the programs' own code: the collector's repositories connect as
/// <c>tp_collector</c>, the API's migrations as <c>tp_api</c>, the trader's two statements as <c>tp_trader</c>, the price watch's readers and alert store as <c>tp_watch</c>. Every other test runs as the
/// superuser and would never notice a missing grant; this is where it shows. The script is the file the deploy runs, not a copy.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DatabaseRolesTests(PostgresFixture postgres)
{
    private const string ApiPassword = "ApiPasswordForTests0123456789";
    private const string CollectorPassword = "CollectorPasswordForTests01234";
    private const string TraderPassword = "TraderPasswordForTests0123456";
    private const string WatchPassword = "WatchPasswordForTests01234567";

    private const string CandleTable = "binance_futures_btcusdt_1h";
    private const string LiquidationTable = "binance_futures_btcusdt_liquidation";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RolesScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotNetTradePulse.slnx")))
            directory = directory.Parent;
        var sql = Path.Combine(directory?.FullName ?? throw new FileNotFoundException("The solution folder was not found."), "deploy", "main-db-roles.sql");

        return $"\\set api_password '{ApiPassword}'\n\\set collector_password '{CollectorPassword}'\n\\set trader_password '{TraderPassword}'\n\\set watch_password '{WatchPassword}'\n{File.ReadAllText(sql)}";
    }

    private async Task<string> NewDatabaseAsync()
    {
        var database = $"roles_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.ExecuteAsync($"CREATE DATABASE {database}");
        return database;
    }

    private string ConnectionString(string database, string? role = null) => role is null
        ? new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString
        : new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = database,
            Username = role,
            Password = role switch { "tp_api" => ApiPassword, "tp_collector" => CollectorPassword, "tp_watch" => WatchPassword, _ => TraderPassword }
        }.ConnectionString;

    private static DbContextOptions<T> Options<T>(string connectionString, string historySchema) where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", historySchema)).Options;

    private static async Task MigrateAllAsync(string connectionString)
    {
        await using (var auth = new AuthDbContext(Options<AuthDbContext>(connectionString, "auth")))
            await auth.Database.MigrateAsync(Ct);
        await using (var config = new ConfigDbContext(Options<ConfigDbContext>(connectionString, "config")))
            await config.Database.MigrateAsync(Ct);
        await using (var research = new ResearchDbContext(Options<ResearchDbContext>(connectionString, "research")))
            await research.Database.MigrateAsync(Ct);
    }

    private static async Task AssertDeniedAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: Ct)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, cancellationToken: Ct))!;
    }

    private static IEnumerable<Candle> Candles(int count)
    {
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count).Select(i =>
            new Candle(start.AddHours(i), 1m, 4m, 0.5m, 3m, 10m, start.AddHours(i + 1).AddMilliseconds(-1), 25m, 42, 6m, 15m));
    }

    /// <summary>A server as it was before the roles: everything made and owned by the superuser, with data in it.</summary>
    private async Task<string> OlderServerAsync()
    {
        var database = await NewDatabaseAsync();
        var superuser = ConnectionString(database);
        await MigrateAllAsync(superuser);

        await using (var config = new ConfigDbContext(Options<ConfigDbContext>(superuser, "config")))
        {
            config.CandleWorkerConfigurations.Add(new CandleWorkerConfiguration
            {
                SymbolListingId = 1, TimeFrameId = (int)TimeFrameEnum._1H, StartDate = new DateTime(2025, 1, 1), CreateDate = new DateTime(2025, 1, 1)
            });
            config.SymbolDataConfigurations.Add(new SymbolDataConfiguration
            {
                SymbolListingId = 1, CollectFundingRate = true, FundingStartDate = new DateTime(2025, 1, 1), CollectLiquidation = true, CreateDate = new DateTime(2025, 1, 1)
            });
            await config.SaveChangesAsync(Ct);
        }

        var candles = new CandleRepository(superuser);
        await candles.EnsureTableExistsAsync(CandleTable, Ct);
        await candles.BulkInsert(CandleTable, Candles(5), Ct);
        var liquidations = new TimeSeriesRepository<Liquidation>(superuser, new LiquidationTable());
        await liquidations.EnsureTableExistsAsync(LiquidationTable, Ct);
        return database;
    }

    [Fact]
    public async Task TheCollectorDoesItsWholeJobAsItsOwnRoleAndSeesNothingElse()
    {
        var database = await OlderServerAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        var asCollector = ConnectionString(database, "tp_collector");

        // What to collect: the two readers the collector's providers use.
        await using (var config = new ConfigDbContext(Options<ConfigDbContext>(asCollector, "config")))
        {
            var series = Assert.Single(await new CandleWorkerConfigurationRepository(config).GetConfigurationList(Ct));
            Assert.Equal("BTCUSDT", series.SymbolListing!.ExchangeSymbol);
            Assert.Single(await new SymbolDataConfigurationRepository(config).GetAll(Ct));
        }

        // The tables it made before the roles existed, and new ones: create-if-missing, write, read back, find gaps.
        var candles = new CandleRepository(asCollector);
        await candles.EnsureTableExistsAsync(CandleTable, Ct);
        await candles.EnsureTableExistsAsync("binance_futures_ethusdt_1h", Ct);
        Assert.Equal(5, (await candles.GetLatestAsync(CandleTable, 10, null, Ct)).Count);
        Assert.Equal(3, await candles.BulkInsert(CandleTable, Candles(8), Ct));
        Assert.NotNull(await candles.GetLatestOpenDataTime(CandleTable, Ct));
        Assert.NotNull(await candles.GetFirstOpenTimeAsync(CandleTable, Ct));
        Assert.Empty(await candles.GetGapsAsync(CandleTable, TimeSpan.FromHours(1), null, Ct));
        Assert.Equal(1, await candles.BulkInsert("binance_futures_ethusdt_1h", Candles(1), Ct));

        var liquidations = new TimeSeriesRepository<Liquidation>(asCollector, new LiquidationTable());
        await liquidations.EnsureTableExistsAsync(LiquidationTable, Ct);   // has an index: needs the table to be its own
        var sample = new Liquidation(new DateTime(2025, 1, 1, 0, 0, 1), OrderSideEnum.Sell, "LIMIT", "IOC", "FILLED", 1m, 100m, 100m, 1m, 1m);
        Assert.Equal(1, await liquidations.BulkInsert(LiquidationTable, [sample], Ct));
        Assert.NotNull(await liquidations.GetLatestTimeAsync(LiquidationTable, Ct));

        // And what it must not be able to do.
        await AssertDeniedAsync(asCollector, "SELECT * FROM auth.users");
        await AssertDeniedAsync(asCollector, "SELECT * FROM auth.user_tokens");
        await AssertDeniedAsync(asCollector, "SELECT * FROM auth.login_throttles");
        await AssertDeniedAsync(asCollector, "SELECT * FROM config.audit_log");
        await AssertDeniedAsync(asCollector, "SELECT * FROM config.traded_symbols");
        await AssertDeniedAsync(asCollector, "SELECT * FROM research.market_events");
        await AssertDeniedAsync(asCollector, "UPDATE config.candle_worker_configurations SET \"CollectOpenInterest\" = true");
        await AssertDeniedAsync(asCollector, "CREATE TABLE config.evil (x int)");
        await AssertDeniedAsync(asCollector, "CREATE SCHEMA evil");
    }

    [Fact]
    public async Task TheApiMigratesAsItsOwnRoleReadsTheDataAndCannotWriteIt()
    {
        var database = await OlderServerAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        var asApi = ConnectionString(database, "tp_api");

        await MigrateAllAsync(asApi);   // what the API does at every start

        await using (var auth = new AuthDbContext(Options<AuthDbContext>(asApi, "auth")))
            Assert.Empty(await auth.Users.ToListAsync(Ct));
        await using (var config = new ConfigDbContext(Options<ConfigDbContext>(asApi, "config")))
            Assert.Single(await config.CandleWorkerConfigurations.ToListAsync(Ct));

        Assert.Equal(5, (await new CandleRepository(asApi).GetLatestAsync(CandleTable, 10, null, Ct)).Count);
        await AssertDeniedAsync(asApi, $"INSERT INTO {CandleTable} (open_time) VALUES (now())");
        await AssertDeniedAsync(asApi, $"DELETE FROM {CandleTable}");
        await AssertDeniedAsync(asApi, $"DROP TABLE {CandleTable}");
        await AssertDeniedAsync(asApi, "CREATE TABLE public.evil (x int)");
    }

    [Fact]
    public async Task TheTraderMayOnlyAddToTheAuditLogAndPublishTheSymbols()
    {
        var database = await OlderServerAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        var asTrader = ConnectionString(database, "tp_trader");

        await using (var connection = new NpgsqlConnection(asTrader))
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO config.audit_log ("OccurredAt", "UserName", "Action", "EntityType", "EntityId", "Summary")
                VALUES (now() at time zone 'utc', 'u', 'Added', 'ExchangeKey', 0, 's')
                """, cancellationToken: Ct));
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM config.traded_symbols", cancellationToken: Ct));
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO config.traded_symbols VALUES ('BTCUSDT', now() at time zone 'utc')", cancellationToken: Ct));
        }

        Assert.Equal(1, await ScalarAsync<long>(ConnectionString(database), "SELECT count(*) FROM config.audit_log WHERE \"UserName\" = 'u'"));
        await AssertDeniedAsync(asTrader, "SELECT * FROM config.audit_log");
        await AssertDeniedAsync(asTrader, "SELECT * FROM config.traded_symbols");
        await AssertDeniedAsync(asTrader, "UPDATE config.audit_log SET \"Summary\" = 'x'");
        await AssertDeniedAsync(asTrader, "SELECT * FROM auth.users");
        await AssertDeniedAsync(asTrader, $"SELECT * FROM {CandleTable}");
        await AssertDeniedAsync(asTrader, "SELECT * FROM config.candle_worker_configurations");
    }

    [Fact]
    public async Task ThePriceWatchDoesItsWholeJobAsItsOwnRoleAndSeesNothingElse()
    {
        var database = await OlderServerAsync();
        var superuser = ConnectionString(database);
        await using (var config = new ConfigDbContext(Options<ConfigDbContext>(superuser, "config")))
        {
            config.ShockWatchConfigurations.Add(new ShockWatchConfiguration { SymbolListingId = 1, Reason = WatchReasonEnum.Manual, IsEnabled = true });
            await config.SaveChangesAsync(Ct);
        }
        await using (var connection = new NpgsqlConnection(superuser))
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO config.traded_symbols VALUES ('ETHUSDT', now() at time zone 'utc')", cancellationToken: Ct));
        await postgres.RunPsqlAsync(database, RolesScript());
        var asWatch = ConnectionString(database, "tp_watch");

        // What to watch: the list and the traded symbols, read the way the program reads them (rows, plus the Futures listings of the traded names).
        await using (var config = new ConfigDbContext(Options<ConfigDbContext>(asWatch, "config")))
        {
            var watched = await new ConfiguredWatchedSymbols(
                new ShockWatchConfigurationRepository(config), new TradedSymbolSource(asWatch), new SymbolCatalogRepository(config)).GetAsync(Ct);
            Assert.Equal(["BTCUSDT", "ETHUSDT"], watched.Select(w => w.Symbol.Value).Order());
        }

        // The seed: the stored candles.
        Assert.Equal(5, (await new CandleRepository(asWatch).GetLatestAsync(CandleTable, 10, null, Ct)).Count);

        // Its alerts: stored when they start, updated when they end.
        var alert = new ShockAlert
        {
            SymbolListingId = 1, TriggeredAt = new DateTime(2025, 1, 1, 12, 30, 0, DateTimeKind.Utc), Direction = EventDirectionEnum.Up, Level = AlertLevelEnum.Attention,
            WindowSeconds = 30, Move = 0.0031m, ZScore = 7m, StartPrice = 100m, EndPrice = 100.31m, Volume = 12m, FlaggedSeconds = 4, RuleVersion = "test"
        };
        await using (var research = new ResearchDbContext(Options<ResearchDbContext>(asWatch, "research")))
        {
            var store = new ShockAlertStore(research);
            await store.SaveAsync(alert, Ct);
            alert.Id = 0;
            alert.Level = AlertLevelEnum.Alarm;
            alert.Move = 0.0052m;
            await store.SaveAsync(alert, Ct);
        }
        Assert.Equal(1, await ScalarAsync<long>(superuser, "SELECT count(*) FROM research.shock_alerts"));
        Assert.Equal(0.0052m, await ScalarAsync<decimal>(superuser, "SELECT \"Move\" FROM research.shock_alerts"));

        // And what it must not be able to do.
        await AssertDeniedAsync(asWatch, "SELECT * FROM auth.users");
        await AssertDeniedAsync(asWatch, "SELECT * FROM auth.user_tokens");
        await AssertDeniedAsync(asWatch, "SELECT * FROM config.audit_log");
        await AssertDeniedAsync(asWatch, "SELECT * FROM research.market_events");
        await AssertDeniedAsync(asWatch, "SELECT * FROM config.candle_worker_configurations");
        await AssertDeniedAsync(asWatch, "DELETE FROM research.shock_alerts");
        await AssertDeniedAsync(asWatch, "UPDATE config.shock_watch_configurations SET \"IsEnabled\" = false");
        await AssertDeniedAsync(asWatch, "DELETE FROM config.traded_symbols");
        await AssertDeniedAsync(asWatch, $"DELETE FROM {CandleTable}");
        await AssertDeniedAsync(asWatch, "CREATE TABLE research.evil (x int)");
        await AssertDeniedAsync(asWatch, "CREATE TABLE public.evil (x int)");
    }

    [Fact]
    public async Task ANewServerGetsItsRolesBeforeTheApiAndTheLastGrantsAfterIt()
    {
        var database = await NewDatabaseAsync();

        await postgres.RunPsqlAsync(database, RolesScript());           // nothing exists yet: the tables are skipped with a notice
        await MigrateAllAsync(ConnectionString(database, "tp_api"));    // the first start of the API, as its own role
        await postgres.RunPsqlAsync(database, RolesScript());           // the grants on the tables it just made

        await using (var config = new ConfigDbContext(Options<ConfigDbContext>(ConnectionString(database, "tp_collector"), "config")))
            Assert.Empty(await new CandleWorkerConfigurationRepository(config).GetConfigurationList(Ct));
        await using var trader = new NpgsqlConnection(ConnectionString(database, "tp_trader"));
        await trader.ExecuteAsync(new CommandDefinition("INSERT INTO config.traded_symbols VALUES ('ETHUSDT', now() at time zone 'utc')", cancellationToken: Ct));
    }

    [Fact]
    public async Task TheScriptCanBeRunAgainAndPutsBackWhatARestoreTookAway()
    {
        var database = await OlderServerAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        await postgres.RunPsqlAsync(database, RolesScript());

        // A restore with --no-owner: everything belongs to whoever restored it, and the grants are gone.
        var superuser = ConnectionString(database);
        await using (var connection = new NpgsqlConnection(superuser))
            await connection.ExecuteAsync(new CommandDefinition($"""
                ALTER TABLE public.{CandleTable} OWNER TO postgres;
                ALTER TABLE config.audit_log OWNER TO postgres;
                REVOKE ALL ON ALL TABLES IN SCHEMA public FROM tp_api;
                REVOKE ALL ON config.audit_log FROM tp_trader;
                """, cancellationToken: Ct));
        await AssertDeniedAsync(ConnectionString(database, "tp_api"), $"SELECT * FROM {CandleTable}");

        await postgres.RunPsqlAsync(database, RolesScript());

        Assert.Equal("tp_collector", await ScalarAsync<string>(superuser, $"SELECT tableowner FROM pg_tables WHERE tablename = '{CandleTable}'"));
        Assert.Equal("tp_api", await ScalarAsync<string>(superuser, "SELECT tableowner FROM pg_tables WHERE tablename = 'audit_log'"));
        Assert.Equal(5, (await new CandleRepository(ConnectionString(database, "tp_api")).GetLatestAsync(CandleTable, 10, null, Ct)).Count);
    }

    [Fact]
    public async Task NoProgramRoleIsMoreThanAPlainLogin()
    {
        var database = await NewDatabaseAsync();
        await postgres.RunPsqlAsync(database, RolesScript());

        var privileged = await ScalarAsync<long>(ConnectionString(database),
            "SELECT count(*) FROM pg_roles WHERE rolname IN ('tp_api','tp_collector','tp_trader','tp_watch') AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls OR NOT rolcanlogin)");
        Assert.Equal(0, privileged);
    }
}
