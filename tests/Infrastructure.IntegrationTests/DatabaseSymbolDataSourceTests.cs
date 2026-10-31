using Application;
using BackgroundWorker.SymbolData;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// The whole path the worker takes to learn which funding rates and liquidations to collect, on a real Postgres: the rows
/// the admin stored, read through the real query handler and dispatcher with the worker's own dependency registrations.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DatabaseSymbolDataSourceTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> NewDatabaseAsync()
    {
        var database = $"symbol_data_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var create = new NpgsqlCommand($"create database {database}", admin);
        await create.ExecuteNonQueryAsync(Ct);

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
    }

    private static async Task MigrateAsync(string connectionString, params SymbolDataConfiguration[] rows)
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "config"))
            .Options;

        await using var context = new ConfigDbContext(options);
        await context.Database.MigrateAsync(Ct);
        context.SymbolDataConfigurations.AddRange(rows);
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
        services.AddSingleton<ISymbolDataSource, DatabaseSymbolDataSource>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static SymbolDataConfiguration Row(SeededSymbol symbol, bool funding, bool liquidation, DateTime? fundingStart = null) => new()
    {
        SymbolListingId = (int)symbol,   // the migration lists every symbol on Binance Futures with the old enum value as its id
        CollectFundingRate = funding,
        FundingStartDate = fundingStart,
        CollectLiquidation = liquidation,
    };

    [Fact]
    public async Task TheStoredRowsAreReadAsPlans()
    {
        var connectionString = await NewDatabaseAsync();
        await MigrateAsync(connectionString,
            Row(SeededSymbol.BTCUSDT, funding: true, liquidation: true, fundingStart: new DateTime(2020, 1, 1)),
            Row(SeededSymbol.ETHUSDT, funding: false, liquidation: true));
        await using var services = WorkerServices(connectionString);

        var plans = await services.GetRequiredService<ISymbolDataSource>().LoadAsync(Ct);

        Assert.Equal<(ExchangeSymbol, bool, DateTime?, bool)>(
            [(SeededSymbol.BTCUSDT.Sym(), true, (DateTime?)new DateTime(2020, 1, 1), true),
             (SeededSymbol.ETHUSDT.Sym(), false, null, true)],
            plans.OrderBy(p => p.Symbol.Value, StringComparer.Ordinal).Select(p => (p.Symbol, p.CollectFundingRate, p.FundingStartDate, p.CollectLiquidation)));
        Assert.All(plans.Where(p => p.FundingStartDate is not null), p => Assert.Equal(DateTimeKind.Utc, p.FundingStartDate!.Value.Kind));
    }

    [Fact]
    public async Task AnEmptyTableIsNoPlansNotAnError()
    {
        var connectionString = await NewDatabaseAsync();
        await MigrateAsync(connectionString);
        await using var services = WorkerServices(connectionString);

        var plans = await services.GetRequiredService<ISymbolDataSource>().LoadAsync(Ct);

        Assert.Empty(plans);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeReadThrowsSoThatItIsNeverTakenForNothingToCollect()
    {
        // No schema at all: reading the table fails, and that has to reach the provider as a failure it retries.
        var connectionString = await NewDatabaseAsync();
        await using var services = WorkerServices(connectionString);

        await Assert.ThrowsAnyAsync<Exception>(() => services.GetRequiredService<ISymbolDataSource>().LoadAsync(Ct));
    }
}
