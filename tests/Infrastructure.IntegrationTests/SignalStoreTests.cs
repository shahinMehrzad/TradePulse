using Dapper;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Infrastructure.IntegrationTests.SignalTestData;

namespace Infrastructure.IntegrationTests;

/// <summary>The store of signals, paper trades and their log, on a real Postgres with the real migration.</summary>
[Collection(PostgresCollection.Name)]
public class SignalStoreTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string _connectionString = string.Empty;

    private async Task<DbContextOptions<SignalsDbContext>> NewDatabaseAsync()
    {
        var database = $"signals_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.ExecuteAsync($"CREATE DATABASE {database}");

        _connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<SignalsDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "signals"))
            .Options;

        await using var context = new SignalsDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    private static SignalStore NewStore(DbContextOptions<SignalsDbContext> options) => new(new SignalsDbContext(options));

    /// <summary>A recorded signal and the trade it opened.</summary>
    private static async Task<PaperTrade> OpenATradeAsync(SignalStore store, TradeSignal? signal = null)
    {
        signal ??= Signal();
        await store.RecordAsync(ReceivedSignal.From(signal, Now, null), Ct);
        var trade = PaperTrade.Open(signal, 100m, Now, openTradesAtEntry: 0);
        await store.AddTradeAsync(trade, Ct);
        return trade;
    }

    [Fact]
    public async Task ASignalIsRecordedOnceHoweverOftenItIsSent()
    {
        var store = NewStore(await NewDatabaseAsync());

        Assert.True(await store.RecordAsync(ReceivedSignal.From(Signal(), Now, null), Ct));
        Assert.False(await store.RecordAsync(ReceivedSignal.From(Signal(), Now.AddSeconds(30), SignalRejectionEnum.SignalTooOld), Ct));

        await using var connection = new NpgsqlConnection(_connectionString);
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM signals.signals", cancellationToken: Ct)));
        // The first one stays as it was: the repeat did not change what was decided.
        Assert.Null(await connection.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT \"Rejection\" FROM signals.signals", cancellationToken: Ct)));
    }

    [Fact]
    public async Task ARefusedSignalIsKeptWithItsReason()
    {
        var options = await NewDatabaseAsync();

        await NewStore(options).RecordAsync(ReceivedSignal.From(Signal(), Now, SignalRejectionEnum.PriceMovedTooFar), Ct);

        await using var context = new SignalsDbContext(options);
        var stored = await context.Signals.AsNoTracking().SingleAsync(Ct);
        Assert.False(stored.WasTaken);
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, stored.Rejection);
    }

    [Fact]
    public async Task ASignalComesBackAsItWasSentWithUtcTimes()
    {
        var options = await NewDatabaseAsync();
        var sent = Signal(PositionSideEnum.Short, maxDuration: TimeSpan.FromHours(48));

        await NewStore(options).RecordAsync(ReceivedSignal.From(sent, Now, null), Ct);

        await using var context = new SignalsDbContext(options);
        var stored = await context.Signals.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(sent, stored.ToTradeSignal());
        Assert.Equal(DateTimeKind.Utc, stored.ReceivedAt.Kind);
        Assert.Equal(Now, stored.ReceivedAt);
    }

    [Fact]
    public async Task ATradeIsStoredWithItsIdAndCounted()
    {
        var store = NewStore(await NewDatabaseAsync());

        var trade = await OpenATradeAsync(store);

        Assert.True(trade.Id > 0);
        Assert.Equal(1, await store.CountOpenAsync(ExchangeEnum.Binance, "S1", "BTC/USDT", Ct));
        Assert.Equal(0, await store.CountOpenAsync(ExchangeEnum.Binance, "S1", "ETH/USDT", Ct));
        Assert.Equal(0, await store.CountOpenAsync(ExchangeEnum.Binance, "S2", "BTC/USDT", Ct));
    }

    [Fact]
    public async Task ATradeNeedsItsSignal()
    {
        var store = NewStore(await NewDatabaseAsync());

        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddTradeAsync(PaperTrade.Open(Signal(), 100m, Now, 0), Ct));
    }

    [Fact]
    public async Task ASignalMakesAtMostOneTrade()
    {
        var store = NewStore(await NewDatabaseAsync());
        await OpenATradeAsync(store);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddTradeAsync(PaperTrade.Open(Signal(), 100m, Now, 0), Ct));
    }

    [Fact]
    public async Task SeveralTradesOfOneStrategyOnOneSymbolCanBeOpenAtOnce()
    {
        var store = NewStore(await NewDatabaseAsync());

        await OpenATradeAsync(store, Signal(candleOpen: CandleOpen));
        await OpenATradeAsync(store, Signal(candleOpen: CandleOpen.AddHours(4)));

        Assert.Equal(2, await store.CountOpenAsync(ExchangeEnum.Binance, "S1", "BTC/USDT", Ct));
    }

    [Fact]
    public async Task OpenTradesComeBackAsTheyWereOpenedOldestFirst()
    {
        var store = NewStore(await NewDatabaseAsync());
        var first = await OpenATradeAsync(store, Signal(candleOpen: CandleOpen, maxDuration: TimeSpan.FromHours(24)));
        await OpenATradeAsync(store, Signal(PositionSideEnum.Short, candleOpen: CandleOpen.AddHours(4)));

        var open = await store.GetOpenTradesAsync(Ct);

        Assert.Equal(2, open.Count);
        var loaded = open[0];
        Assert.Equal(first.Id, loaded.Id);
        Assert.Equal((first.SignalId, first.Side, first.Leverage), (loaded.SignalId, loaded.Side, loaded.Leverage));
        Assert.Equal((104m, 98m, 100m, 100m), (loaded.TakeProfit, loaded.StopLoss, loaded.SignalPrice, loaded.EntryPrice));
        Assert.Equal(Now, loaded.OpenedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.OpenedAt.Kind);
        Assert.Equal(Now.AddHours(24), loaded.ExpiresAt);
        Assert.Equal(PositionSideEnum.Short, open[1].Side);
        Assert.True(loaded.IsOpen);
    }

    [Fact]
    public async Task AnOpenTradeLoadedFromTheDatabaseKeepsFollowingThePrice()
    {
        var store = NewStore(await NewDatabaseAsync());
        await OpenATradeAsync(store);

        var loaded = Assert.Single(await store.GetOpenTradesAsync(Ct));

        Assert.False(loaded.Observe(101m, Now.AddMinutes(1)));
        Assert.True(loaded.Observe(104.5m, Now.AddMinutes(2)));
        Assert.Equal(PaperTradeStatusEnum.TakeProfit, loaded.Status);
    }

    [Fact]
    public async Task ATradeEndsOnceAndKeepsHowItEnded()
    {
        var options = await NewDatabaseAsync();
        var store = NewStore(options);
        await OpenATradeAsync(store);
        var trade = Assert.Single(await store.GetOpenTradesAsync(Ct));
        trade.Observe(99m, Now.AddHours(1));
        trade.Observe(104.5m, Now.AddHours(2));
        trade.Settle([new FundingRate(Now.AddHours(1), 0.0001m, 101m)]);

        Assert.True(await store.CloseAsync(trade, Ct));
        Assert.False(await store.CloseAsync(trade, Ct));

        await using var context = new SignalsDbContext(options);
        var stored = await context.PaperTrades.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(PaperTradeStatusEnum.TakeProfit, stored.Status);
        Assert.Equal(104.5m, stored.ExitPrice);
        Assert.Equal(Now.AddHours(2), stored.ClosedAt);
        Assert.Equal(DateTimeKind.Utc, stored.ClosedAt!.Value.Kind);
        Assert.Equal((104.5m, 99m), (stored.BestPrice, stored.WorstPrice));
        Assert.Equal(trade.NetResult, stored.NetResult);
        Assert.Equal(trade.GrossResult, stored.GrossResult);
        Assert.Equal(trade.FeesPaid, stored.FeesPaid);
        Assert.Equal(trade.FundingPaid, stored.FundingPaid);
        Assert.Equal(trade.RMultiple, stored.RMultiple);
    }

    [Fact]
    public async Task OfTwoCallersThatEndTheSameTradeOnlyOneWins()
    {
        var options = await NewDatabaseAsync();
        await OpenATradeAsync(NewStore(options));

        async Task<bool> EndItAsync()
        {
            var store = NewStore(options);
            var trade = Assert.Single(await store.GetOpenTradesAsync(Ct));
            trade.Observe(104.5m, Now.AddHours(1));
            return await store.CloseAsync(trade, Ct);
        }

        var results = await Task.WhenAll(EndItAsync(), EndItAsync(), EndItAsync());

        Assert.Equal(1, results.Count(won => won));
    }

    [Fact]
    public async Task AnEndedTradeIsNotOpenAnyMore()
    {
        var store = NewStore(await NewDatabaseAsync());
        await OpenATradeAsync(store);
        var trade = Assert.Single(await store.GetOpenTradesAsync(Ct));
        trade.Observe(97m, Now.AddHours(1));
        await store.CloseAsync(trade, Ct);

        Assert.Empty(await store.GetOpenTradesAsync(Ct));
        Assert.Equal(0, await store.CountOpenAsync(ExchangeEnum.Binance, "S1", "BTC/USDT", Ct));
    }

    [Fact]
    public async Task AMessageIsAttachedOnce()
    {
        var options = await NewDatabaseAsync();
        var store = NewStore(options);
        var trade = await OpenATradeAsync(store);

        Assert.True(await store.AttachMessageAsync(trade.Id, -1001234567890, 42, Ct));
        Assert.False(await store.AttachMessageAsync(trade.Id, -1001234567890, 43, Ct));

        var stored = Assert.Single(await store.GetOpenTradesAsync(Ct));
        Assert.Equal((-1001234567890, 42), (stored.TelegramChatId, stored.TelegramMessageId));
    }

    [Fact]
    public async Task TheLogKeepsWhatHappenedToASignalInOrder()
    {
        var options = await NewDatabaseAsync();
        var store = NewStore(options);
        var id = Signal().SignalId;

        await store.AddEventAsync(new SignalEvent { SignalId = id, At = Now, Kind = SignalEventKindEnum.Received }, Ct);
        await store.AddEventAsync(new SignalEvent { SignalId = id, At = Now.AddSeconds(1), Kind = SignalEventKindEnum.Opened, Detail = "entry 100" }, Ct);

        await using var context = new SignalsDbContext(options);
        var events = await context.Events.AsNoTracking().Where(e => e.SignalId == id).OrderBy(e => e.At).ToListAsync(Ct);
        Assert.Equal([SignalEventKindEnum.Received, SignalEventKindEnum.Opened], events.Select(e => e.Kind));
        Assert.Equal("entry 100", events[1].Detail);
        Assert.Equal(DateTimeKind.Utc, events[0].At.Kind);
    }

    [Fact]
    public async Task TheDatabaseRefusesATradeThatEndedWithoutAnExit()
    {
        var store = NewStore(await NewDatabaseAsync());
        await OpenATradeAsync(store);

        await using var connection = new NpgsqlConnection(_connectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync(new CommandDefinition("UPDATE signals.paper_trades SET \"Status\" = 3", cancellationToken: Ct)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task TheDatabaseRefusesALeverageBelowOne()
    {
        var store = NewStore(await NewDatabaseAsync());
        var received = ReceivedSignal.From(Signal(leverage: 0.5m), Now, null);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.RecordAsync(received, Ct));
    }
}
