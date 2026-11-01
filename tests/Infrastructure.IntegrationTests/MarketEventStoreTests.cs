using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>The research schema and the store that fills it, on a real Postgres with the real migration.</summary>
[Collection(PostgresCollection.Name)]
public class MarketEventStoreTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<DbContextOptions<ResearchDbContext>> NewDatabaseAsync()
    {
        var database = $"research_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var create = new NpgsqlCommand($"create database {database}", admin);
        await create.ExecuteNonQueryAsync(Ct);

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<ResearchDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "research"))
            .Options;

        await using var context = new ResearchDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    // Every call gets its own context, like every request or command run does.
    private static MarketEventStore StoreOn(DbContextOptions<ResearchDbContext> options) => new(new ResearchDbContext(options));

    /// <param name="kind">Candles are read as UTC, so events made from them carry UTC; the store has to cope with both.</param>
    private static MarketEvent Event(int hours, int listing = 1, string version = "m5-test-v1", DateTimeKind kind = DateTimeKind.Utc, params int[] horizons) => new()
    {
        SymbolListingId = listing,
        StartedAt = DateTime.SpecifyKind(Day.AddHours(hours), kind),
        Direction = EventDirectionEnum.Down,
        FiveMinuteReturn = -0.0312m,
        ZScore = 9.5m,
        VolumeRatio = 42.25m,
        FlaggedMinutes = 3,
        DetectorVersion = version,
        Reactions = horizons.Select(h => new EventReaction { HorizonMinutes = h, Return = -0.01m * h, MaxRise = 0.002m, MaxFall = -0.02m }).ToList(),
    };

    [Fact]
    public async Task TheTablesAreInTheResearchSchema()
    {
        var options = await NewDatabaseAsync();

        await using var context = new ResearchDbContext(options);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        await context.Database.OpenConnectionAsync(Ct);
        command.CommandText = "select table_name from information_schema.tables where table_schema = 'research' order by 1";

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            tables.Add(reader.GetString(0));

        Assert.Equal(["__EFMigrationsHistory", "event_reactions", "market_events"], tables);
    }

    [Fact]
    public async Task StoresAnEventWithItsReactionsAndReadsThemBack()
    {
        var options = await NewDatabaseAsync();

        var added = await StoreOn(options).AddNewAsync([Event(5, horizons: [1, 5, 60])], Ct);
        var stored = Assert.Single(await StoreOn(options).ListAsync(1, null, null, Ct));

        Assert.Equal(1, added);
        Assert.Equal(Day.AddHours(5), stored.StartedAt);
        Assert.Equal((EventDirectionEnum.Down, -0.0312m, 9.5m, 42.25m, 3, "m5-test-v1"),
            (stored.Direction, stored.FiveMinuteReturn, stored.ZScore, stored.VolumeRatio, stored.FlaggedMinutes, stored.DetectorVersion));
        Assert.Equal([1, 5, 60], stored.Reactions.OrderBy(r => r.HorizonMinutes).Select(r => r.HorizonMinutes));
        Assert.Equal(-0.6m, stored.Reactions.Single(r => r.HorizonMinutes == 60).Return);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task TakesTimesOfEitherKindBecauseTheColumnHasNoZone(DateTimeKind kind)
    {
        var options = await NewDatabaseAsync();

        await StoreOn(options).AddNewAsync([Event(5, kind: kind)], Ct);

        var found = await StoreOn(options).ListAsync(1, DateTime.SpecifyKind(Day, kind), DateTime.SpecifyKind(Day.AddDays(1), kind), Ct);
        Assert.Single(found);
    }

    [Fact]
    public async Task SavingTheSameEventsAgainAddsNothing()
    {
        var options = await NewDatabaseAsync();
        await StoreOn(options).AddNewAsync([Event(5, horizons: [1])], Ct);

        var addedAgain = await StoreOn(options).AddNewAsync([Event(5, horizons: [1])], Ct);

        Assert.Equal(0, addedAgain);
        Assert.Single(await StoreOn(options).ListAsync(1, null, null, Ct));
    }

    [Fact]
    public async Task OnlyTheEventsNotStoredYetAreAdded()
    {
        var options = await NewDatabaseAsync();
        await StoreOn(options).AddNewAsync([Event(5), Event(9)], Ct);

        var added = await StoreOn(options).AddNewAsync([Event(5), Event(7), Event(9), Event(11)], Ct);

        Assert.Equal(2, added);
        Assert.Equal([5, 7, 9, 11], (await StoreOn(options).ListAsync(1, null, null, Ct)).Select(e => (int)(e.StartedAt - Day).TotalHours));
    }

    [Fact]
    public async Task TheSameMomentFoundByAnotherVersionOrOfAnotherListingIsADifferentEvent()
    {
        var options = await NewDatabaseAsync();

        var added = await StoreOn(options).AddNewAsync([Event(5), Event(5, version: "m5-test-v2"), Event(5, listing: 2)], Ct);

        Assert.Equal(3, added);
        Assert.Equal(2, (await StoreOn(options).ListAsync(1, null, null, Ct)).Count);
        Assert.Single(await StoreOn(options).ListAsync(2, null, null, Ct));
    }

    [Fact]
    public async Task ListsOneListingInTheRangeOldestFirst()
    {
        var options = await NewDatabaseAsync();
        await StoreOn(options).AddNewAsync([Event(9), Event(3), Event(6), Event(6, listing: 2), Event(30)], Ct);

        var found = await StoreOn(options).ListAsync(1, Day.AddHours(4), Day.AddHours(24), Ct);

        Assert.Equal([6, 9], found.Select(e => (int)(e.StartedAt - Day).TotalHours));
        Assert.All(found, e => Assert.Equal(1, e.SymbolListingId));
    }

    [Fact]
    public async Task NothingToAddIsNotAnError()
    {
        var options = await NewDatabaseAsync();

        Assert.Equal(0, await StoreOn(options).AddNewAsync([], Ct));
    }
}
