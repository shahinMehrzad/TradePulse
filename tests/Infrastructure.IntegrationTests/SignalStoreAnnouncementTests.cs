using Dapper;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Infrastructure.IntegrationTests.SignalTestData;

namespace Infrastructure.IntegrationTests;

/// <summary>What is still to be told in the chat, found again after a restart, on a real Postgres.</summary>
[Collection(PostgresCollection.Name)]
public class SignalStoreAnnouncementTests(PostgresFixture postgres)
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

    private static async Task<PaperTrade> OpenAsync(SignalStore store, DateTime candleOpen)
    {
        var signal = Signal(candleOpen: candleOpen);
        await store.RecordAsync(ReceivedSignal.From(signal, Now, null), Ct);
        var trade = PaperTrade.Open(signal, 100m, Now, 0);
        await store.AddTradeAsync(trade, Ct);
        return trade;
    }

    private static async Task EndAsync(SignalStore store, PaperTrade stored, DateTime at)
    {
        var trade = (await store.GetOpenTradesAsync(Ct)).Single(t => t.Id == stored.Id);
        trade.Observe(104.5m, at);
        Assert.True(await store.CloseAsync(trade, Ct));
    }

    [Fact]
    public async Task AnOpenTradeWithoutAMessageIsFoundUntilItGetsOne()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));
        var trade = await OpenAsync(store, CandleOpen);
        Assert.Equal(trade.Id, Assert.Single(await store.GetOpenWithoutMessageAsync(Ct)).Id);

        Assert.True(await store.AttachMessageAsync(trade.Id, -1001234567890, 42, Ct));

        Assert.Empty(await store.GetOpenWithoutMessageAsync(Ct));
    }

    [Fact]
    public async Task ATradeThatHasEndedIsNotAmongTheOpenOnesWithoutAMessage()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));
        await EndAsync(store, await OpenAsync(store, CandleOpen), Now.AddHours(1));

        Assert.Empty(await store.GetOpenWithoutMessageAsync(Ct));
    }

    [Fact]
    public async Task AnEndIsFoundUntilItIsMarkedAsToldAndIsMarkedOnce()
    {
        var options = await NewDatabaseAsync();
        var store = new SignalStore(new SignalsDbContext(options));
        var trade = await OpenAsync(store, CandleOpen);
        await EndAsync(store, trade, Now.AddHours(1));
        Assert.Equal(trade.Id, Assert.Single(await store.GetEndsToAnnounceAsync(Ct)).Id);

        Assert.True(await store.MarkEndAnnouncedAsync(trade.Id, Now.AddHours(2), Ct));
        Assert.False(await store.MarkEndAnnouncedAsync(trade.Id, Now.AddHours(3), Ct));

        Assert.Empty(await store.GetEndsToAnnounceAsync(Ct));
        await using var context = new SignalsDbContext(options);
        var stored = await context.PaperTrades.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Now.AddHours(2), stored.EndAnnouncedAt);
        Assert.Equal(DateTimeKind.Utc, stored.EndAnnouncedAt!.Value.Kind);
    }

    [Fact]
    public async Task ATradeThatIsStillOpenCannotBeMarkedAsEnded()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));
        var trade = await OpenAsync(store, CandleOpen);

        Assert.False(await store.MarkEndAnnouncedAsync(trade.Id, Now.AddHours(2), Ct));

        Assert.Empty(await store.GetEndsToAnnounceAsync(Ct));
    }

    [Fact]
    public async Task TheEndsToTellComeOldestFirst()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));
        var late = await OpenAsync(store, CandleOpen);
        var early = await OpenAsync(store, CandleOpen.AddHours(4));
        await EndAsync(store, late, Now.AddHours(5));
        await EndAsync(store, early, Now.AddHours(2));

        var ends = await store.GetEndsToAnnounceAsync(Ct);

        Assert.Equal([early.Id, late.Id], ends.Select(t => t.Id));
    }

    [Fact]
    public async Task ATradeLoadedFromTheDatabaseKnowsItsMessage()
    {
        var store = new SignalStore(new SignalsDbContext(await NewDatabaseAsync()));
        var trade = await OpenAsync(store, CandleOpen);
        await store.AttachMessageAsync(trade.Id, -1001234567890, 42, Ct);
        await EndAsync(store, trade, Now.AddHours(1));

        var loaded = Assert.Single(await store.GetEndsToAnnounceAsync(Ct));

        Assert.Equal((-1001234567890, 42), (loaded.TelegramChatId, loaded.TelegramMessageId));
        Assert.Equal(PaperTradeStatusEnum.TakeProfit, loaded.Status);
    }
}
