using Dapper;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Infrastructure.IntegrationTests.SignalTestData;

namespace Infrastructure.IntegrationTests;

/// <summary>Two exchanges in the signals' tables. Only Binance exists today: the other is a number that is not a member of the enum yet.</summary>
[Collection(PostgresCollection.Name)]
public class SignalStoreExchangeTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const ExchangeEnum Another = (ExchangeEnum)2;

    private async Task<DbContextOptions<SignalsDbContext>> NewDatabaseAsync()
    {
        var database = $"signals_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.ExecuteAsync($"CREATE DATABASE {database}");

        var options = new DbContextOptionsBuilder<SignalsDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "signals"))
            .Options;

        await using var context = new SignalsDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    private static async Task OpenAsync(SignalStore store, Domain.ValueObjects.TradeSignal signal)
    {
        await store.RecordAsync(ReceivedSignal.From(signal, Now, null), Ct);
        await store.AddTradeAsync(PaperTrade.Open(signal, 100m, Now, 0), Ct);
    }

    [Fact]
    public async Task TheSameStrategySymbolAndCandleOnTwoExchangesAreTwoSignalsAndTwoTrades()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));

        await OpenAsync(store, Signal(exchange: ExchangeEnum.Binance));
        await OpenAsync(store, Signal(exchange: Another));

        Assert.Equal(1, await store.CountOpenAsync(ExchangeEnum.Binance, "S1", "BTC/USDT", Ct));
        Assert.Equal(1, await store.CountOpenAsync(Another, "S1", "BTC/USDT", Ct));
    }

    [Fact]
    public async Task TheExchangeComesBackWithTheSignalAndTheTrade()
    {
        var options = await NewDatabaseAsync();
        var store = new SignalStore(new SignalsDbContext(options));
        var signal = Signal(exchange: Another);
        await OpenAsync(store, signal);

        await using var context = new SignalsDbContext(options);
        var received = await context.Signals.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Another, received.Exchange);
        Assert.Equal(signal, received.ToTradeSignal());
        Assert.Equal(Another, (await store.GetOpenTradesAsync(Ct)).Single().Exchange);
    }

    [Fact]
    public async Task ARowWrittenWithoutAnExchangeIsBinance()
    {
        var options = await NewDatabaseAsync();
        await using var context = new SignalsDbContext(options);
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(Ct);

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO signals.signals ("SignalId", "Strategy", "Market", "Symbol", "Side", "Leverage", "TakeProfit", "StopLoss", "SignalPrice", "TimeFrame",
                "CandleOpenTime", "CandleCloseTime", "ValidUntil", "ReceivedAt")
            VALUES ('old', 'S1', 2, 'BTC/USDT', 1, 5, 104, 98, 100, 14400, now(), now(), now(), now())
            """, cancellationToken: Ct));

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT \"Exchange\" FROM signals.signals", cancellationToken: Ct)));
    }
}
