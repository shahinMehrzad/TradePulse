using Dapper;
using Domain.Entities;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Infrastructure.IntegrationTests.SignalTestData;

namespace Infrastructure.IntegrationTests;

/// <summary>How far each open trade has been followed, saved and loaded again, on a real Postgres.</summary>
[Collection(PostgresCollection.Name)]
public class SignalStoreProgressTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static async Task<PaperTrade> OpenAsync(SignalStore store, Domain.ValueObjects.TradeSignal? signal = null)
    {
        signal ??= Signal();
        await store.RecordAsync(ReceivedSignal.From(signal, Now, null), Ct);
        var trade = PaperTrade.Open(signal, 100m, Now, 0);
        await store.AddTradeAsync(trade, Ct);
        return trade;
    }

    [Fact]
    public async Task ANewTradeIsLoadedWithItsEntryAsTheLastCheck()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));
        await OpenAsync(store);

        var loaded = Assert.Single(await store.GetOpenTradesAsync(Ct));

        Assert.Equal(Now, loaded.LastCheckedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.LastCheckedAt!.Value.Kind);
    }

    [Fact]
    public async Task TheProgressOfOpenTradesIsSavedAndLoadedAgain()
    {
        var options = await NewDatabaseAsync();
        var store = new SignalStore(new SignalsDbContext(options));
        await OpenAsync(store, Signal(candleOpen: CandleOpen));
        await OpenAsync(store, Signal(candleOpen: CandleOpen.AddHours(4)));
        var trades = (await store.GetOpenTradesAsync(Ct)).ToList();

        trades[0].Observe(103m, Now.AddMinutes(1));
        trades[0].Observe(99m, Now.AddMinutes(2));
        trades[0].MarkChecked(Now.AddMinutes(30));
        await store.SaveProgressAsync(trades, Ct);

        var reloaded = (await new SignalStore(new SignalsDbContext(options)).GetOpenTradesAsync(Ct)).ToList();
        Assert.Equal(Now.AddMinutes(30), reloaded[0].LastCheckedAt);
        Assert.Equal((103m, 99m), (reloaded[0].BestPrice, reloaded[0].WorstPrice));
        Assert.Equal(Now, reloaded[1].LastCheckedAt);
    }

    [Fact]
    public async Task ATradeThatEndedMeanwhileIsNotTouchedBySavingProgress()
    {
        var options = await NewDatabaseAsync();
        var store = new SignalStore(new SignalsDbContext(options));
        await OpenAsync(store);
        var stale = Assert.Single(await store.GetOpenTradesAsync(Ct));      // what the watcher holds

        var other = Assert.Single(await store.GetOpenTradesAsync(Ct));      // another caller ends it
        other.Observe(104.5m, Now.AddMinutes(1));
        Assert.True(await store.CloseAsync(other, Ct));

        stale.MarkChecked(Now.AddMinutes(30));
        stale.Observe(101m, Now.AddMinutes(31));
        await store.SaveProgressAsync([stale], Ct);

        await using var context = new SignalsDbContext(options);
        var stored = await context.PaperTrades.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Domain.Enums.PaperTradeStatusEnum.TakeProfit, stored.Status);
        Assert.Equal(104.5m, stored.BestPrice);
        Assert.Equal(Now, stored.LastCheckedAt);
    }
}
