using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>The watch list of the config schema, on a real Postgres with the real migrations.</summary>
[Collection(PostgresCollection.Name)]
public class ShockWatchConfigurationTableTests(PostgresFixture postgres)
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

    private static async Task SaveAsync(DbContextOptions<ConfigDbContext> options, params ShockWatchConfiguration[] rows)
    {
        await using var context = new ConfigDbContext(options);
        context.ShockWatchConfigurations.AddRange(rows);
        await context.SaveChangesAsync(Ct);
    }

    private static ShockWatchConfiguration Row(int listing = BtcListing, WatchReasonEnum reason = WatchReasonEnum.Manual, bool enabled = true) =>
        new() { SymbolListingId = listing, Reason = reason, IsEnabled = enabled };

    [Fact]
    public async Task ARowIsStoredAndReadBackWithItsReasonAndSwitch()
    {
        var options = await NewDatabaseAsync();

        await SaveAsync(options, Row(reason: WatchReasonEnum.Trading, enabled: false));

        await using var context = new ConfigDbContext(options);
        var stored = await context.ShockWatchConfigurations.AsNoTracking().Include(c => c.SymbolListing).SingleAsync(Ct);
        Assert.Equal((BtcListing, WatchReasonEnum.Trading, false), (stored.SymbolListingId, stored.Reason, stored.IsEnabled));
        Assert.Equal("BTCUSDT", stored.SymbolListing!.ExchangeSymbol);
        Assert.True(stored.CreateDate > DateTime.UtcNow.AddMinutes(-5), "the database fills the creation time");
    }

    [Fact]
    public async Task TheReasonIsStoredAsItsNumberBecauseThatIsTheContract()
    {
        var options = await NewDatabaseAsync();
        await SaveAsync(options, Row(BtcListing, WatchReasonEnum.Manual), Row(EthListing, WatchReasonEnum.Trading));

        await using var context = new ConfigDbContext(options);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        await context.Database.OpenConnectionAsync(Ct);
        command.CommandText = "select \"Reason\" from config.shock_watch_configurations order by \"SymbolListingId\"";

        var reasons = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            reasons.Add(reader.GetInt32(0));

        Assert.Equal([1, 2], reasons);
        Assert.Equal([1, 2], Enum.GetValues<WatchReasonEnum>().Select(r => (int)r));
    }

    [Fact]
    public async Task ASymbolCanBeOnTheListOnlyOnce()
    {
        var options = await NewDatabaseAsync();
        await SaveAsync(options, Row());

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(options, Row(reason: WatchReasonEnum.Trading)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)duplicate.InnerException!).SqlState);
    }

    [Fact]
    public async Task AListingThatDoesNotExistCannotBeWatched()
    {
        var options = await NewDatabaseAsync();

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(options, Row(listing: 999_999)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)refused.InnerException!).SqlState);
    }

    [Fact]
    public async Task AListingOnTheListCannotBeDeletedBehindTheWatchsBack()
    {
        var options = await NewDatabaseAsync();
        await SaveAsync(options, Row());

        await using var context = new ConfigDbContext(options);
        var listing = await context.SymbolListings.SingleAsync(l => l.Id == BtcListing, Ct);
        context.SymbolListings.Remove(listing);

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)refused.InnerException!).SqlState);
    }
}
