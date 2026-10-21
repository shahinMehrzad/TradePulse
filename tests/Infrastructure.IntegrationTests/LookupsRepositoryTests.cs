using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>The config schema on a real Postgres, created by the real migrations (a fresh database per test).</summary>
[Collection(PostgresCollection.Name)]
public class LookupsRepositoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ConfigDbContext> MigratedContextAsync()
    {
        var database = $"config_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"create database {database}", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "config"))
            .Options;

        var context = new ConfigDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return context;
    }

    [Fact]
    public async Task TheLookupTablesMirrorTheEnumsExactly()
    {
        await using var context = await MigratedContextAsync();

        Assert.Equal(
            Enum.GetValues<ExchangeEnum>().Select(v => ((int)v, v.ToString())),
            (await context.Exchanges.OrderBy(x => x.Id).ToListAsync(Ct)).Select(x => (x.Id, x.Name)));
        Assert.Equal(
            Enum.GetValues<MarketTypeEnum>().Select(v => ((int)v, v.ToString())),
            (await context.Markets.OrderBy(x => x.Id).ToListAsync(Ct)).Select(x => (x.Id, x.Name)));
        Assert.Equal(
            Enum.GetValues<SymbolEnum>().Select(v => ((int)v, v.ToString())),
            (await context.Symbols.OrderBy(x => x.Id).ToListAsync(Ct)).Select(x => (x.Id, x.Name)));
        Assert.Equal(
            Enum.GetValues<TimeFrameEnum>().Select(v => ((int)v, v.ToString(), (int)v)),
            (await context.TimeFrames.OrderBy(x => x.Id).ToListAsync(Ct)).Select(x => (x.Id, x.Name, x.IntervalToSeconds)));
    }

    [Fact]
    public async Task TheMigrationsMatchTheModel()
    {
        // Fails when an enum member (or any model change) was added without a new migration.
        await using var context = await MigratedContextAsync();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ExchangesAndMarketsAreListed()
    {
        await using var context = await MigratedContextAsync();
        var repository = new LookupsRepository(context);

        Assert.Equal(["Binance"], (await repository.GetExchangeListAsync(Ct)).Select(x => x.Name));
        Assert.Equal(["Spot", "Futures"], (await repository.GetMarketListAsync(Ct)).OrderBy(x => x.Id).Select(x => x.Name));
    }

    [Theory]
    [InlineData("btc", new[] { "BTCUSDT" })]
    [InlineData("BTC", new[] { "BTCUSDT" })]
    [InlineData("  btc  ", new[] { "BTCUSDT" })]
    [InlineData("usdt", new[] { "ADAUSDT", "BNBUSDT", "BTCUSDT", "ETHUSDT", "SOLUSDT", "SUIUSDT", "XAUUSDT", "XRPUSDT" })]
    [InlineData("nothing-like-this", new string[0])]
    public async Task SymbolSearchIgnoresCaseAndSortsByName(string keyword, string[] expected)
    {
        await using var context = await MigratedContextAsync();

        var result = await new LookupsRepository(context).GetSymbolListAsync(keyword, Ct);

        Assert.Equal(expected, result.Select(x => x.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankKeywordListsEverySymbol(string? keyword)
    {
        await using var context = await MigratedContextAsync();

        var result = await new LookupsRepository(context).GetSymbolListAsync(keyword, Ct);

        Assert.Equal(Enum.GetValues<SymbolEnum>().Length, result.Count());
    }

    [Fact]
    public async Task SymbolSearchAlsoLooksAtTheDescriptionWhenThereIsOne()
    {
        await using var context = await MigratedContextAsync();
        var bitcoin = await context.Symbols.SingleAsync(x => x.Name == "BTCUSDT", Ct);
        bitcoin.Description = "BitCoin";
        await context.SaveChangesAsync(Ct);
        var repository = new LookupsRepository(context);

        // Matches through the description only; every other symbol has a null description and must simply not match.
        Assert.Equal(["BTCUSDT"], (await repository.GetSymbolListAsync("bitcoin", Ct)).Select(x => x.Name));
        Assert.Equal(["BTCUSDT"], (await repository.GetSymbolListAsync("COIN", Ct)).Select(x => x.Name));
    }

    [Theory]
    [InlineData(0, int.MaxValue, 13)]
    [InlineData(3600, 14400, 3)]          // 1h, 2h, 4h: both ends are inclusive
    [InlineData(0, 900, 5)]               // 1s, 1m, 3m, 5m, 15m
    [InlineData(86400, int.MaxValue, 1)]  // 1d
    [InlineData(5000, 5000, 0)]
    public async Task TimeFramesAreFilteredByIntervalInclusively(int min, int max, int expectedCount)
    {
        await using var context = await MigratedContextAsync();

        var result = (await new LookupsRepository(context).GetTimeFrameListAsync(min, max, Ct)).ToList();

        Assert.Equal(expectedCount, result.Count);
        Assert.Equal(result.OrderBy(x => x.IntervalToSeconds).Select(x => x.Id), result.Select(x => x.Id));
        Assert.All(result, x => Assert.InRange(x.IntervalToSeconds, min, max));
    }

    [Fact]
    public async Task ASeriesCanOnlyBeConfiguredOnce()
    {
        await using var context = await MigratedContextAsync();
        CandleWorkerConfiguration Series() => new()
        {
            ExchangeId = (int)ExchangeEnum.Binance,
            MarketId = (int)MarketTypeEnum.Futures,
            SymbolId = (int)SymbolEnum.BTCUSDT,
            TimeFrameId = (int)TimeFrameEnum._4H,
            StartDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
        };

        context.CandleWorkerConfigurations.Add(Series());
        await context.SaveChangesAsync(Ct);
        context.CandleWorkerConfigurations.Add(Series());

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ALookupRowInUseCannotBeDeletedFromTheDatabase()
    {
        await using var context = await MigratedContextAsync();
        context.CandleWorkerConfigurations.Add(new CandleWorkerConfiguration
        {
            ExchangeId = (int)ExchangeEnum.Binance,
            MarketId = (int)MarketTypeEnum.Spot,
            SymbolId = (int)SymbolEnum.ETHUSDT,
            TimeFrameId = (int)TimeFrameEnum._1H,
            StartDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
        });
        await context.SaveChangesAsync(Ct);

        // Straight to SQL: through the change tracker EF would refuse before the database is even asked.
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => context.Symbols.Where(x => x.Name == "ETHUSDT").ExecuteDeleteAsync(Ct));

        var postgres = failure as PostgresException ?? failure.InnerException as PostgresException;
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres?.SqlState);
    }

    private static CandleWorkerConfiguration NewSeries(DateTime startDate, int symbolId = (int)SymbolEnum.BTCUSDT, int exchangeId = (int)ExchangeEnum.Binance) => new()
    {
        ExchangeId = exchangeId,
        MarketId = (int)MarketTypeEnum.Futures,
        SymbolId = symbolId,
        TimeFrameId = (int)TimeFrameEnum._4H,
        StartDate = startDate
    };

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task AddingASeriesAcceptsAUtcDateOfEitherKindAndReturnsTheNewId(DateTimeKind kind)
    {
        await using var context = await MigratedContextAsync();
        var repository = new CandleWorkerConfigurationRepository(context);
        var start = new DateTime(2020, 1, 1, 0, 0, 0, kind);

        var id = await repository.AddNewCandleWorkerConfiguration(NewSeries(start), Ct);

        Assert.True(id > 0);
        var stored = Assert.Single(await new CandleWorkerConfigurationRepository(context).GetConfigurations(Ct));
        Assert.Equal((id, new DateTime(2020, 1, 1)), (stored.Id, stored.StartDate));
    }

    [Fact]
    public async Task AddingTheSameSeriesTwiceIsAConflictEvenWhenNoOneCheckedFirst()
    {
        await using var context = await MigratedContextAsync();
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await new CandleWorkerConfigurationRepository(context).AddNewCandleWorkerConfiguration(NewSeries(start), Ct);

        // A second context stands for the second of two simultaneous requests: it never saw the first row.
        await using var other = await ContextOnSameDatabaseAsync(context);

        await Assert.ThrowsAsync<ConfigurationConflictException>(
            () => new CandleWorkerConfigurationRepository(other).AddNewCandleWorkerConfiguration(NewSeries(start), Ct));
    }

    [Fact]
    public async Task AddingASeriesWithAnUnknownIdIsAValidationErrorNotASilentZero()
    {
        await using var context = await MigratedContextAsync();
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => new CandleWorkerConfigurationRepository(context).AddNewCandleWorkerConfiguration(NewSeries(start, exchangeId: 99), Ct));
        Assert.Empty(await new CandleWorkerConfigurationRepository(context).GetConfigurations(Ct));
    }

    [Fact]
    public async Task UpdatingASeriesWritesTheNewValuesAndKeepsTheCreationDate()
    {
        await using var context = await MigratedContextAsync();
        var repository = new CandleWorkerConfigurationRepository(context);
        var id = await repository.AddNewCandleWorkerConfiguration(NewSeries(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Ct);
        var created = (await repository.GetConfigurations(Ct)).Single().CreateDate;

        var change = NewSeries(new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc), symbolId: (int)SymbolEnum.ETHUSDT);
        change.Id = id;

        var updated = await repository.UpdateCandleWorkerConfiguration(change, Ct);

        Assert.Equal((id, (int)SymbolEnum.ETHUSDT, new DateTime(2021, 6, 1)), (updated.Id, updated.SymbolId, updated.StartDate));
        var stored = (await new CandleWorkerConfigurationRepository(context).GetConfigurations(Ct)).Single();
        Assert.Equal((id, (int)SymbolEnum.ETHUSDT, new DateTime(2021, 6, 1), created), (stored.Id, stored.SymbolId, stored.StartDate, stored.CreateDate));
    }

    [Fact]
    public async Task UpdatingAMissingSeriesIsNotFound()
    {
        await using var context = await MigratedContextAsync();
        var missing = NewSeries(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        missing.Id = 12345;

        await Assert.ThrowsAsync<ConfigurationNotFoundException>(
            () => new CandleWorkerConfigurationRepository(context).UpdateCandleWorkerConfiguration(missing, Ct));
    }

    [Fact]
    public async Task RemovingASeriesDeletesOnlyThatRow()
    {
        await using var context = await MigratedContextAsync();
        var repository = new CandleWorkerConfigurationRepository(context);
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var doomed = await repository.AddNewCandleWorkerConfiguration(NewSeries(start), Ct);
        var survivor = await repository.AddNewCandleWorkerConfiguration(NewSeries(start, symbolId: (int)SymbolEnum.ETHUSDT), Ct);

        await repository.RemoveCandleWorkerConfiguration(doomed, Ct);

        var left = await new CandleWorkerConfigurationRepository(context).GetConfigurations(Ct);
        Assert.Equal([survivor], left.Select(c => c.Id));
    }

    [Fact]
    public async Task RemovingAMissingSeriesIsNotFound()
    {
        await using var context = await MigratedContextAsync();

        await Assert.ThrowsAsync<ConfigurationNotFoundException>(
            () => new CandleWorkerConfigurationRepository(context).RemoveCandleWorkerConfiguration(12345, Ct));
    }

    private static Task<ConfigDbContext> ContextOnSameDatabaseAsync(ConfigDbContext context)
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseNpgsql(context.Database.GetConnectionString(), npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "config"))
            .Options;
        return Task.FromResult(new ConfigDbContext(options));
    }
}
