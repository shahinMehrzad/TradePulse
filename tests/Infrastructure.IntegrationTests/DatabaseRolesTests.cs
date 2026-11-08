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
    private const string MonitorPassword = "MonitorPasswordForTests012345";
    private const string SignalsPassword = "SignalsPasswordForTests0123456";
    private const string SignalSourcePassword = "SignalSourcePasswordForTests012";

    private const string CandleTable = "binance_futures_btcusdt_1h";
    private const string LiquidationTable = "binance_futures_btcusdt_liquidation";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="monitorPassword">Empty: the script makes no tp_monitor (monitoring is optional).</param>
    private static string RolesScript(string monitorPassword = MonitorPassword, string signalsPassword = SignalsPassword, string signalSourcePassword = SignalSourcePassword)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotNetTradePulse.slnx")))
            directory = directory.Parent;
        var sql = Path.Combine(directory?.FullName ?? throw new FileNotFoundException("The solution folder was not found."), "deploy", "main-db-roles.sql");

        return $"\\set api_password '{ApiPassword}'\n\\set collector_password '{CollectorPassword}'\n\\set trader_password '{TraderPassword}'\n\\set watch_password '{WatchPassword}'\n\\set monitor_password '{monitorPassword}'\n\\set signals_password '{signalsPassword}'\n\\set signal_source_password '{signalSourcePassword}'\n{File.ReadAllText(sql)}";
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
            Password = role switch { "tp_api" => ApiPassword, "tp_collector" => CollectorPassword, "tp_watch" => WatchPassword, "tp_monitor" => MonitorPassword,
                "tp_signals" => SignalsPassword, "tp_signal_source" => SignalSourcePassword, _ => TraderPassword }
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
        await using (var signals = new SignalsDbContext(Options<SignalsDbContext>(connectionString, "signals")))
            await signals.Database.MigrateAsync(Ct);
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
    public async Task TheMonitorReadsTheStatisticsOfTheServerAndNothingElse()
    {
        var database = await OlderServerAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        var asMonitor = ConnectionString(database, "tp_monitor");

        // What a monitoring agent asks: who is connected, what is read and written, how big things are, the settings.
        Assert.True(await ScalarAsync<long>(asMonitor, "SELECT count(*) FROM pg_stat_activity") >= 1);
        Assert.True(await ScalarAsync<long>(asMonitor, "SELECT count(*) FROM pg_stat_database WHERE datname = current_database()") == 1);
        Assert.True(await ScalarAsync<long>(asMonitor, "SELECT pg_database_size(current_database())") > 0);
        Assert.False(string.IsNullOrEmpty(await ScalarAsync<string>(asMonitor, "SHOW max_connections")));

        // Not a row of anything: no account, no setting of the collector, no candle, no alert; and nothing can be changed or made.
        await AssertDeniedAsync(asMonitor, "SELECT * FROM auth.users");
        await AssertDeniedAsync(asMonitor, "SELECT * FROM auth.user_tokens");
        await AssertDeniedAsync(asMonitor, "SELECT * FROM config.audit_log");
        await AssertDeniedAsync(asMonitor, "SELECT * FROM config.candle_worker_configurations");
        await AssertDeniedAsync(asMonitor, "SELECT * FROM research.shock_alerts");
        await AssertDeniedAsync(asMonitor, $"SELECT * FROM {CandleTable}");
        await AssertDeniedAsync(asMonitor, "DELETE FROM config.traded_symbols");
        await AssertDeniedAsync(asMonitor, "CREATE TABLE public.evil (x int)");
        await AssertDeniedAsync(asMonitor, "CREATE SCHEMA evil");
    }

    [Fact]
    public async Task AnEmptyMonitorPasswordNeitherMakesNorChangesTheMonitorAndTheOtherRolesAreStillMade()
    {
        // Roles belong to the whole server, not to one database, so a monitor made by an earlier test is still here: what can be told is
        // that an empty password does not blank its password (monitoring is optional, a script run without it must not lock the agent out).
        var database = await NewDatabaseAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        await postgres.RunPsqlAsync(database, RolesScript(monitorPassword: ""));

        Assert.Equal(1, await ScalarAsync<int>(ConnectionString(database, "tp_monitor"), "SELECT 1"));   // still the old password
        Assert.Equal(4, await ScalarAsync<long>(ConnectionString(database), "SELECT count(*) FROM pg_roles WHERE rolname IN ('tp_api','tp_collector','tp_trader','tp_watch')"));
    }

    [Fact]
    public async Task TheMonitorsPasswordCanBeChangedByRunningTheScriptAgain()
    {
        var database = await NewDatabaseAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        const string next = "ANewMonitorPasswordForTests01";

        await postgres.RunPsqlAsync(database, RolesScript(monitorPassword: next));

        var changed = new NpgsqlConnectionStringBuilder(ConnectionString(database, "tp_monitor")) { Password = next }.ConnectionString;
        Assert.Equal(1, await ScalarAsync<int>(changed, "SELECT 1"));
        await using var old = new NpgsqlConnection(ConnectionString(database, "tp_monitor"));
        await Assert.ThrowsAsync<PostgresException>(() => old.OpenAsync(Ct));
    }

    [Fact]
    public async Task TheSignalServiceDoesItsWholeJobAsItsOwnRoleAndDeletesNothing()
    {
        var database = await OlderServerAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        var asSignals = ConnectionString(database, "tp_signals");

        // Its whole job, with the program's own store: record a signal (once), open the trade, count, load it again, attach the message, end it.
        await using (var context = new SignalsDbContext(Options<SignalsDbContext>(asSignals, "signals")))
        {
            var store = new SignalStore(context);
            var signal = SignalTestData.Signal();

            Assert.True(await store.RecordAsync(ReceivedSignal.From(signal, SignalTestData.Now, null), Ct));
            Assert.False(await store.RecordAsync(ReceivedSignal.From(signal, SignalTestData.Now, null), Ct));

            var trade = PaperTrade.Open(signal, 100m, SignalTestData.Now, openTradesAtEntry: 0);
            await store.AddTradeAsync(trade, Ct);
            Assert.Equal(1, await store.CountOpenAsync("S1", "BTC/USDT", Ct));
            Assert.True(await store.AttachMessageAsync(trade.Id, -1001234567890, 42, Ct));

            var loaded = Assert.Single(await store.GetOpenTradesAsync(Ct));
            loaded.Observe(104.5m, SignalTestData.Now.AddHours(1));
            loaded.Settle([]);
            Assert.True(await store.CloseAsync(loaded, Ct));
            Assert.False(await store.CloseAsync(loaded, Ct));

            await store.AddEventAsync(new SignalEvent { SignalId = signal.SignalId, At = SignalTestData.Now, Kind = SignalEventKindEnum.Closed }, Ct);
        }

        // What it reads besides: the symbol lists, the strategies it takes signals from and the stored candles.
        Assert.True(await ScalarAsync<long>(asSignals, "SELECT count(*) FROM config.exchange_symbols") > 0);
        await using (var config = new ConfigDbContext(Options<ConfigDbContext>(asSignals, "config")))
            Assert.Empty(await new SignalStrategyConfigurationRepository(config).GetAll(Ct));
        Assert.Equal(5, (await new CandleRepository(asSignals).GetLatestAsync(CandleTable, 10, null, Ct)).Count);

        // And what it must not be able to do: delete or rewrite what it stored, see the rest, change the configuration or the data.
        await AssertDeniedAsync(asSignals, "DELETE FROM signals.paper_trades");
        await AssertDeniedAsync(asSignals, "DELETE FROM signals.signals");
        await AssertDeniedAsync(asSignals, "DELETE FROM signals.events");
        await AssertDeniedAsync(asSignals, "UPDATE signals.signals SET \"Strategy\" = 'x'");
        await AssertDeniedAsync(asSignals, "UPDATE signals.events SET \"Detail\" = 'x'");
        await AssertDeniedAsync(asSignals, "UPDATE config.signal_strategy_configurations SET \"IsEnabled\" = true");
        await AssertDeniedAsync(asSignals, "DELETE FROM config.signal_strategy_configurations");
        await AssertDeniedAsync(asSignals, "CREATE TABLE signals.evil (x int)");
        await AssertDeniedAsync(asSignals, "CREATE TABLE public.evil (x int)");
        await AssertDeniedAsync(asSignals, "SELECT * FROM auth.users");
        await AssertDeniedAsync(asSignals, "SELECT * FROM auth.user_tokens");
        await AssertDeniedAsync(asSignals, "SELECT * FROM config.audit_log");
        await AssertDeniedAsync(asSignals, "SELECT * FROM config.traded_symbols");
        await AssertDeniedAsync(asSignals, "SELECT * FROM research.market_events");
        await AssertDeniedAsync(asSignals, "UPDATE config.candle_worker_configurations SET \"CollectOpenInterest\" = true");
        await AssertDeniedAsync(asSignals, $"DELETE FROM {CandleTable}");
    }

    [Fact]
    public async Task TheSignalSourceReadsTheDataTablesAndNothingElse()
    {
        var database = await OlderServerAsync();
        await postgres.RunPsqlAsync(database, RolesScript());
        var asSource = ConnectionString(database, "tp_signal_source");

        Assert.Equal(5, (await new CandleRepository(asSource).GetLatestAsync(CandleTable, 10, null, Ct)).Count);

        // Not a signal, not a trade, not a setting, not an account; and no way to change a candle or make a table.
        await AssertDeniedAsync(asSource, "SELECT * FROM signals.signals");
        await AssertDeniedAsync(asSource, "SELECT * FROM signals.paper_trades");
        await AssertDeniedAsync(asSource, "SELECT * FROM signals.events");
        await AssertDeniedAsync(asSource, "INSERT INTO signals.events (\"SignalId\", \"At\", \"Kind\") VALUES ('x', now(), 1)");
        await AssertDeniedAsync(asSource, "SELECT * FROM config.exchange_symbols");
        await AssertDeniedAsync(asSource, "SELECT * FROM config.signal_strategy_configurations");
        await AssertDeniedAsync(asSource, "SELECT * FROM config.candle_worker_configurations");
        await AssertDeniedAsync(asSource, "SELECT * FROM config.audit_log");
        await AssertDeniedAsync(asSource, "SELECT * FROM auth.users");
        await AssertDeniedAsync(asSource, "SELECT * FROM research.shock_alerts");
        await AssertDeniedAsync(asSource, $"DELETE FROM {CandleTable}");
        await AssertDeniedAsync(asSource, $"INSERT INTO {CandleTable} (open_time) VALUES (now())");
        await AssertDeniedAsync(asSource, "CREATE TABLE public.evil (x int)");
        await AssertDeniedAsync(asSource, "CREATE SCHEMA evil");
    }

    [Fact]
    public async Task TheSignalRolesAreOptionalAndTheScriptRunsWithoutTheirPasswords()
    {
        var database = await NewDatabaseAsync();

        // The script is run by a server that has not set the two passwords: no error, and the other roles are made as always.
        await postgres.RunPsqlAsync(database, RolesScript(signalsPassword: "", signalSourcePassword: ""));
        await MigrateAllAsync(ConnectionString(database, "tp_api"));
        await postgres.RunPsqlAsync(database, RolesScript(signalsPassword: "", signalSourcePassword: ""));

        Assert.Equal(1, await ScalarAsync<int>(ConnectionString(database, "tp_api"), "SELECT 1"));
        Assert.Equal(0, await ScalarAsync<long>(ConnectionString(database, "tp_api"), "SELECT count(*) FROM signals.signals"));
    }

    [Fact]
    public async Task TheSignalRolesGetTheirGrantsOnTablesTheApiMadeAfterThem()
    {
        var database = await NewDatabaseAsync();

        await postgres.RunPsqlAsync(database, RolesScript());           // the signal tables do not exist yet: skipped with a notice
        await MigrateAllAsync(ConnectionString(database, "tp_api"));
        await postgres.RunPsqlAsync(database, RolesScript());           // the grants on what the API just made

        await using var connection = new NpgsqlConnection(ConnectionString(database, "tp_signals"));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO signals.events (\"SignalId\", \"At\", \"Kind\") VALUES ('x', now() at time zone 'utc', 1)", cancellationToken: Ct));
        Assert.Equal("tp_api", await ScalarAsync<string>(ConnectionString(database), "SELECT tableowner FROM pg_tables WHERE tablename = 'paper_trades'"));
    }

    [Fact]
    public async Task NoProgramRoleIsMoreThanAPlainLogin()
    {
        var database = await NewDatabaseAsync();
        await postgres.RunPsqlAsync(database, RolesScript());

        var privileged = await ScalarAsync<long>(ConnectionString(database),
            "SELECT count(*) FROM pg_roles WHERE rolname IN ('tp_api','tp_collector','tp_trader','tp_watch','tp_monitor','tp_signals','tp_signal_source') AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls OR NOT rolcanlogin)");
        Assert.Equal(0, privileged);
    }
}
