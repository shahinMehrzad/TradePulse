using Dapper;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Infrastructure.IntegrationTests.SignalTestData;

namespace Infrastructure.IntegrationTests;

/// <summary>A signal and its trade stored together, and a signal found again by its id, on a real Postgres.</summary>
[Collection(PostgresCollection.Name)]
public class SignalStoreIntakeTests(PostgresFixture postgres)
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

    private static SignalStore NewStore(DbContextOptions<SignalsDbContext> options) => new(new SignalsDbContext(options));

    private static async Task<long> CountAsync(DbContextOptions<SignalsDbContext> options, string table)
    {
        await using var context = new SignalsDbContext(options);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT count(*) FROM signals.{table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task ATakenSignalAndItsTradeAreStoredTogether()
    {
        var options = await NewDatabaseAsync();
        var signal = Signal();
        var trade = PaperTrade.Open(signal, 100.2m, Now, 1);

        Assert.True(await NewStore(options).RecordAsync(ReceivedSignal.From(signal, Now, null), trade, Ct));

        Assert.True(trade.Id > 0);
        Assert.Equal((1L, 1L), (await CountAsync(options, "signals"), await CountAsync(options, "paper_trades")));
        var stored = Assert.Single(await NewStore(options).GetOpenTradesAsync(Ct));
        Assert.Equal((signal.SignalId, 100.2m, 1), (stored.SignalId, stored.EntryPrice, stored.OpenTradesAtEntry));
    }

    [Fact]
    public async Task ARefusedSignalIsStoredWithoutATrade()
    {
        var options = await NewDatabaseAsync();

        Assert.True(await NewStore(options).RecordAsync(ReceivedSignal.From(Signal(), Now, SignalRejectionEnum.PriceMovedTooFar), null, Ct));

        Assert.Equal((1L, 0L), (await CountAsync(options, "signals"), await CountAsync(options, "paper_trades")));
    }

    [Fact]
    public async Task ASignalRecordedBeforeChangesNothingAndItsSecondTradeIsNotStored()
    {
        var options = await NewDatabaseAsync();
        var signal = Signal();
        await NewStore(options).RecordAsync(ReceivedSignal.From(signal, Now, null), PaperTrade.Open(signal, 100m, Now, 0), Ct);

        var again = await NewStore(options).RecordAsync(ReceivedSignal.From(signal, Now.AddSeconds(5), null), PaperTrade.Open(signal, 100.5m, Now.AddSeconds(5), 1), Ct);

        Assert.False(again);
        Assert.Equal((1L, 1L), (await CountAsync(options, "signals"), await CountAsync(options, "paper_trades")));
        Assert.Equal(100m, Assert.Single(await NewStore(options).GetOpenTradesAsync(Ct)).EntryPrice);
    }

    [Fact]
    public async Task IfTheTradeCannotBeStoredTheSignalIsNotStoredEither()
    {
        var options = await NewDatabaseAsync();
        var signal = Signal(leverage: 5);
        var broken = PaperTrade.Open(Signal(leverage: 0.5m) with { SignalId = signal.SignalId }, 100m, Now, 0);   // the database refuses a leverage below one

        await Assert.ThrowsAsync<DbUpdateException>(() => NewStore(options).RecordAsync(ReceivedSignal.From(signal, Now, null), broken, Ct));

        Assert.Equal((0L, 0L), (await CountAsync(options, "signals"), await CountAsync(options, "paper_trades")));
        // So the same signal can be taken when it is sent again.
        Assert.True(await NewStore(options).RecordAsync(ReceivedSignal.From(signal, Now, null), PaperTrade.Open(signal, 100m, Now, 0), Ct));
    }

    [Fact]
    public async Task AFewRequestsOfOneSignalAtTheSameMomentStoreOneSignalAndOneTrade()
    {
        var options = await NewDatabaseAsync();
        var signal = Signal();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            NewStore(options).RecordAsync(ReceivedSignal.From(signal, Now, null), PaperTrade.Open(signal, 100m, Now, 0), Ct)));

        Assert.Equal(1, results.Count(stored => stored));
        Assert.Equal((1L, 1L), (await CountAsync(options, "signals"), await CountAsync(options, "paper_trades")));
    }

    [Fact]
    public async Task ASignalIsFoundByItsIdWithItsDecision()
    {
        var options = await NewDatabaseAsync();
        var signal = Signal(PositionSideEnum.Short);
        await NewStore(options).RecordAsync(ReceivedSignal.From(signal, Now, SignalRejectionEnum.TooManyOpenTrades), null, Ct);

        var found = await NewStore(options).FindSignalAsync(signal.SignalId, Ct);

        Assert.NotNull(found);
        Assert.Equal(SignalRejectionEnum.TooManyOpenTrades, found.Rejection);
        Assert.Equal(signal, found.ToTradeSignal());
        Assert.Null(await NewStore(options).FindSignalAsync("another-id", Ct));
    }
}
