using Application;
using BackgroundWorker.CandleSeries;
using Domain.Entities;
using Domain.Enums;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// The whole path the worker takes to learn what to collect, on a real Postgres: the rows the admin stored, read through the
/// real query handler and dispatcher with the worker's own dependency registrations, mapped to series.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DatabaseCandleSeriesSourceTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> NewDatabaseAsync()
    {
        var database = $"series_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var create = new NpgsqlCommand($"create database {database}", admin);
        await create.ExecuteNonQueryAsync(Ct);

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
    }

    private static async Task MigrateAsync(string connectionString, params CandleWorkerConfiguration[] rows)
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "config"))
            .Options;

        await using var context = new ConfigDbContext(options);
        await context.Database.MigrateAsync(Ct);
        context.CandleWorkerConfigurations.AddRange(rows);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>The worker's registrations: application handlers, infrastructure (including the config DbContext) and the source.</summary>
    private static ServiceProvider WorkerServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddInfrastructureServices(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString })
            .Build());
        services.AddSingleton<ICandleSeriesSource, DatabaseCandleSeriesSource>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static CandleWorkerConfiguration Row(MarketTypeEnum market, SymbolEnum symbol, TimeFrameEnum timeFrame, DateTime start) => new()
    {
        ExchangeId = (int)ExchangeEnum.Binance,
        MarketId = (int)market,
        SymbolId = (int)symbol,
        TimeFrameId = (int)timeFrame,
        StartDate = start,
    };

    [Fact]
    public async Task TheStoredRowsAreReadAsSeries()
    {
        var connectionString = await NewDatabaseAsync();
        await MigrateAsync(connectionString,
            Row(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._4H, new DateTime(2020, 1, 1)),
            Row(MarketTypeEnum.Spot, SymbolEnum.ETHUSDT, TimeFrameEnum._1H, new DateTime(2021, 5, 5)));
        await using var services = WorkerServices(connectionString);

        var series = await services.GetRequiredService<ICandleSeriesSource>().LoadAsync(Ct);

        Assert.Equal(
            [(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._4H, new DateTime(2020, 1, 1)),
             (MarketTypeEnum.Spot, SymbolEnum.ETHUSDT, TimeFrameEnum._1H, new DateTime(2021, 5, 5))],
            series.Select(s => (s.MarketType, s.Name, s.Interval, s.StartDate)).OrderBy(s => s.MarketType == MarketTypeEnum.Spot));
        Assert.All(series, s => Assert.Equal(DateTimeKind.Utc, s.StartDate.Kind));
    }

    [Fact]
    public async Task EverySeriesIsReadNotJustTheFirstPageOfThem()
    {
        // The admin list is paged (10 by default); the worker must never see only a page of what it is meant to collect.
        var connectionString = await NewDatabaseAsync();
        var rows = Enum.GetValues<TimeFrameEnum>().Select(timeFrame =>
            Row(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, timeFrame, new DateTime(2020, 1, 1))).ToArray();
        await MigrateAsync(connectionString, rows);
        await using var services = WorkerServices(connectionString);

        var series = await services.GetRequiredService<ICandleSeriesSource>().LoadAsync(Ct);

        Assert.Equal(Enum.GetValues<TimeFrameEnum>().Length, series.Count);
        Assert.True(series.Count > 10);
    }

    [Fact]
    public async Task AnEmptyTableIsNoSeriesNotAnError()
    {
        var connectionString = await NewDatabaseAsync();
        await MigrateAsync(connectionString);
        await using var services = WorkerServices(connectionString);

        var series = await services.GetRequiredService<ICandleSeriesSource>().LoadAsync(Ct);

        Assert.Empty(series);
    }

    [Fact]
    public async Task ASchemaThatWasNeverCreatedIsAFailureNotSilentlyEmpty()
    {
        // WebApi creates the schema (it is the only one that migrates). A worker started before that must keep waiting
        // and report the problem, not treat "cannot read" as "nothing configured".
        var connectionString = await NewDatabaseAsync();
        await using var services = WorkerServices(connectionString);

        await Assert.ThrowsAnyAsync<Exception>(() => services.GetRequiredService<ICandleSeriesSource>().LoadAsync(Ct));
    }
}
