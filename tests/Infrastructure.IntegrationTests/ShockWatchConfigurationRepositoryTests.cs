using Application.Research.Watch;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>The watch list repository, and the symbols the watch reads from it, on a real Postgres with the real migrations.</summary>
[Collection(PostgresCollection.Name)]
public class ShockWatchConfigurationRepositoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The first migrations list these on Binance Futures with the ids the symbol enum had (1 is BTCUSDT, 3 is ETHUSDT).
    private const int BtcListing = 1;
    private const int EthListing = 3;

    private async Task<DbContextOptions<ConfigDbContext>> NewDatabaseAsync()
    {
        var database = $"config_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var create = new NpgsqlCommand($"create database {database}", admin);
        await create.ExecuteNonQueryAsync(Ct);

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "config"))
            .Options;

        await using var context = new ConfigDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    // Every call gets its own context, like every request does.
    private static ShockWatchConfigurationRepository RepositoryOn(DbContextOptions<ConfigDbContext> options) => new(new ConfigDbContext(options));

    private static ShockWatchConfiguration Row(int listing, WatchReasonEnum reason = WatchReasonEnum.Manual, bool enabled = true) =>
        new() { SymbolListingId = listing, Reason = reason, IsEnabled = enabled };

    [Fact]
    public async Task ARowIsAddedAndReadBackWithItsListing()
    {
        var options = await NewDatabaseAsync();

        var id = await RepositoryOn(options).Add(Row(BtcListing, WatchReasonEnum.Trading), Ct);

        var stored = await RepositoryOn(options).GetById(id, Ct);
        Assert.NotNull(stored);
        Assert.Equal((BtcListing, WatchReasonEnum.Trading, true), (stored.SymbolListingId, stored.Reason, stored.IsEnabled));
        Assert.Equal("BTCUSDT", stored.SymbolListing!.ExchangeSymbol);
        Assert.Equal((int)MarketTypeEnum.Futures, stored.SymbolListing.MarketId);
    }

    [Fact]
    public async Task TheListIsInTheOrderOfTheExchangeNames()
    {
        var options = await NewDatabaseAsync();
        await RepositoryOn(options).Add(Row(EthListing), Ct);
        await RepositoryOn(options).Add(Row(BtcListing), Ct);

        var all = await RepositoryOn(options).GetAll(Ct);

        Assert.Equal(["BTCUSDT", "ETHUSDT"], all.Select(c => c.SymbolListing!.ExchangeSymbol));
    }

    [Fact]
    public async Task ASymbolAlreadyOnTheListIsAConflict()
    {
        var options = await NewDatabaseAsync();
        await RepositoryOn(options).Add(Row(BtcListing), Ct);

        await Assert.ThrowsAsync<ConfigurationConflictException>(() => RepositoryOn(options).Add(Row(BtcListing, WatchReasonEnum.Trading), Ct));
    }

    [Fact]
    public async Task AListingThatDoesNotExistIsRefusedAsInvalid()
    {
        var options = await NewDatabaseAsync();

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => RepositoryOn(options).Add(Row(999_999), Ct));
    }

    [Fact]
    public async Task SwitchingChangesOnlyTheSwitch()
    {
        var options = await NewDatabaseAsync();
        var id = await RepositoryOn(options).Add(Row(BtcListing, WatchReasonEnum.Trading), Ct);

        await RepositoryOn(options).SetEnabled(id, false, Ct);

        var stored = await RepositoryOn(options).GetById(id, Ct);
        Assert.Equal((WatchReasonEnum.Trading, false), (stored!.Reason, stored.IsEnabled));
    }

    [Fact]
    public async Task ARowIsRemoved()
    {
        var options = await NewDatabaseAsync();
        var id = await RepositoryOn(options).Add(Row(BtcListing), Ct);

        await RepositoryOn(options).Remove(id, Ct);

        Assert.Empty(await RepositoryOn(options).GetAll(Ct));
        Assert.Null(await RepositoryOn(options).GetById(id, Ct));
    }

    [Fact]
    public async Task SwitchingOrRemovingARowThatDoesNotExistIsNotFound()
    {
        var options = await NewDatabaseAsync();

        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => RepositoryOn(options).SetEnabled(404, true, Ct));
        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => RepositoryOn(options).Remove(404, Ct));
    }

    [Fact]
    public async Task OneContextAskedAgainAndAgainDoesNotCollectWhatItRead()
    {
        var options = await NewDatabaseAsync();
        await RepositoryOn(options).Add(Row(BtcListing), Ct);
        await using var context = new ConfigDbContext(options);
        var repository = new ShockWatchConfigurationRepository(context);

        for (var i = 0; i < 5; i++)
            await repository.GetAll(Ct);

        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ASwitchMadeInThePanelIsSeenByTheNextLookOfTheWatch()
    {
        var options = await NewDatabaseAsync();
        await using var watchContext = new ConfigDbContext(options);
        var source = new ConfiguredWatchedSymbols(new ShockWatchConfigurationRepository(watchContext));
        Assert.Empty(await source.GetAsync(Ct));

        var btc = await RepositoryOn(options).Add(Row(BtcListing), Ct);
        await RepositoryOn(options).Add(Row(EthListing), Ct);
        var followed = await source.GetAsync(Ct);
        Assert.Equal(["BTCUSDT", "ETHUSDT"], followed.Select(s => s.Symbol.Value));
        Assert.All(followed, s => Assert.Equal((ExchangeEnum.Binance, MarketTypeEnum.Futures), (s.Exchange, s.MarketType)));

        await RepositoryOn(options).SetEnabled(btc, false, Ct);
        Assert.Equal(["ETHUSDT"], (await source.GetAsync(Ct)).Select(s => s.Symbol.Value));
        Assert.Equal(EthListing, Assert.Single(await source.GetAsync(Ct)).SymbolListingId);
    }
}
