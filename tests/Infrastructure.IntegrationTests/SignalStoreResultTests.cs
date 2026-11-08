using Dapper;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Infrastructure.IntegrationTests.SignalTestData;

namespace Infrastructure.IntegrationTests;

/// <summary>The result of an ended paper trade: stored once, and found again when it is missing, on a real Postgres.</summary>
[Collection(PostgresCollection.Name)]
public class SignalStoreResultTests(PostgresFixture postgres)
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

    /// <summary>A recorded signal and the trade it opened.</summary>
    private static async Task<PaperTrade> OpenAsync(SignalStore store, DateTime candleOpen)
    {
        var signal = Signal(candleOpen: candleOpen);
        await store.RecordAsync(ReceivedSignal.From(signal, Now, null), Ct);
        var trade = PaperTrade.Open(signal, 100m, Now, 0);
        await store.AddTradeAsync(trade, Ct);
        return trade;
    }

    /// <summary>The trade as the store holds it, ended at the target at <paramref name="at"/> and stored as ended.</summary>
    private static async Task<PaperTrade> EndedAsync(SignalStore store, PaperTrade stored, DateTime at)
    {
        var trade = (await store.GetOpenTradesAsync(Ct)).Single(t => t.Id == stored.Id);
        trade.Observe(104.5m, at);
        Assert.True(await store.CloseAsync(trade, Ct));
        return trade;
    }

    [Fact]
    public async Task AnEndedTradeIsUnsettledUntilItsResultIsSaved()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));
        var trade = await EndedAsync(store, await OpenAsync(store, CandleOpen), Now.AddHours(1));
        Assert.Equal(trade.Id, Assert.Single(await store.GetUnsettledAsync(Ct)).Id);

        trade.Settle([new FundingRate(Now.AddMinutes(10), 0.0001m, 101m)]);
        Assert.True(await store.SaveResultAsync(trade, Ct));

        Assert.Empty(await store.GetUnsettledAsync(Ct));
    }

    [Fact]
    public async Task TheResultIsStoredWhole()
    {
        var options = await NewDatabaseAsync();
        var store = new SignalStore(new SignalsDbContext(options));
        var trade = await EndedAsync(store, await OpenAsync(store, CandleOpen), Now.AddHours(1));
        trade.Settle([new FundingRate(Now.AddMinutes(10), 0.0001m, 101m)]);

        await store.SaveResultAsync(trade, Ct);

        await using var context = new SignalsDbContext(options);
        var stored = await context.PaperTrades.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((trade.GrossResult, trade.FeesPaid, trade.FundingPaid, trade.NetResult, trade.RMultiple),
            (stored.GrossResult, stored.FeesPaid, stored.FundingPaid, stored.NetResult, stored.RMultiple));
        Assert.Equal(0.000505m, stored.FundingPaid);
    }

    [Fact]
    public async Task AResultIsStoredOnlyOnceAndTheFirstOneStays()
    {
        var options = await NewDatabaseAsync();
        var store = new SignalStore(new SignalsDbContext(options));
        var trade = await EndedAsync(store, await OpenAsync(store, CandleOpen), Now.AddHours(1));
        trade.Settle([]);
        Assert.True(await store.SaveResultAsync(trade, Ct));
        var first = trade.NetResult;

        trade.Settle([new FundingRate(Now.AddMinutes(10), 0.01m, 100m)]);   // another caller worked it out differently
        Assert.False(await store.SaveResultAsync(trade, Ct));

        await using var context = new SignalsDbContext(options);
        Assert.Equal(first, (await context.PaperTrades.AsNoTracking().SingleAsync(Ct)).NetResult);
    }

    [Fact]
    public async Task ATradeThatIsStillOpenHasNoResultSaved()
    {
        var options = await NewDatabaseAsync();
        var store = new SignalStore(new SignalsDbContext(options));
        await OpenAsync(store, CandleOpen);
        var open = Assert.Single(await store.GetOpenTradesAsync(Ct));

        Assert.False(await store.SaveResultAsync(open, Ct));

        Assert.Empty(await store.GetUnsettledAsync(Ct));
        await using var context = new SignalsDbContext(options);
        Assert.Null((await context.PaperTrades.AsNoTracking().SingleAsync(Ct)).NetResult);
    }

    [Fact]
    public async Task TheUnsettledTradesComeOldestEndFirst()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));
        var later = await OpenAsync(store, CandleOpen);
        var earlier = await OpenAsync(store, CandleOpen.AddHours(4));
        await EndedAsync(store, later, Now.AddHours(5));
        await EndedAsync(store, earlier, Now.AddHours(2));

        var unsettled = await store.GetUnsettledAsync(Ct);

        Assert.Equal([earlier.Id, later.Id], unsettled.Select(t => t.Id));
    }
}
