using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>The shock alerts table of the research schema, on a real Postgres with the real migration.</summary>
[Collection(PostgresCollection.Name)]
public class ShockAlertTableTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Moment = new(2026, 10, 29, 20, 34, 15, DateTimeKind.Utc);

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

    private static ShockAlert Alert(int listing = 1, string version = "s30-0.003-z6-v1", DateTimeKind kind = DateTimeKind.Utc) => new()
    {
        SymbolListingId = listing,
        TriggeredAt = DateTime.SpecifyKind(Moment, kind),
        Direction = EventDirectionEnum.Down,
        Level = AlertLevelEnum.Alarm,
        WindowSeconds = 30,
        Move = -0.0062m,
        ZScore = -6.8m,
        StartPrice = 79893.4m,
        EndPrice = 79398.1m,
        Volume = 123.456m,
        FlaggedSeconds = 4,
        RuleVersion = version,
    };

    private static async Task SaveAsync(DbContextOptions<ResearchDbContext> options, ShockAlert alert)
    {
        await using var context = new ResearchDbContext(options);
        context.ShockAlerts.Add(alert);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheTableIsInTheResearchSchemaNextToTheEventTables()
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

        Assert.Equal(["__EFMigrationsHistory", "event_reactions", "market_events", "shock_alerts"], tables);
    }

    [Fact]
    public async Task StoresAnAlertAndReadsEveryFieldBack()
    {
        var options = await NewDatabaseAsync();

        await SaveAsync(options, Alert());

        await using var context = new ResearchDbContext(options);
        var stored = await context.ShockAlerts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Moment, stored.TriggeredAt);
        Assert.Equal(DateTimeKind.Utc, stored.TriggeredAt.Kind);
        Assert.Equal((EventDirectionEnum.Down, AlertLevelEnum.Alarm, 30, -0.0062m, -6.8m), (stored.Direction, stored.Level, stored.WindowSeconds, stored.Move, stored.ZScore));
        Assert.Equal((79893.4m, 79398.1m, 123.456m, 4, "s30-0.003-z6-v1"), (stored.StartPrice, stored.EndPrice, stored.Volume, stored.FlaggedSeconds, stored.RuleVersion));
        Assert.True(stored.CreateDate > DateTime.UtcNow.AddMinutes(-5), "the database fills the creation time");
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task TakesTimesOfEitherKindBecauseTheColumnHasNoZone(DateTimeKind kind)
    {
        var options = await NewDatabaseAsync();

        await SaveAsync(options, Alert(kind: kind));

        await using var context = new ResearchDbContext(options);
        var from = DateTime.SpecifyKind(Moment.AddMinutes(-1), kind);
        Assert.Single(await context.ShockAlerts.Where(a => a.TriggeredAt >= from).ToListAsync(Ct));
    }

    [Fact]
    public async Task TheSameAlertTwiceIsRefusedSoAReplayCannotDuplicateIt()
    {
        var options = await NewDatabaseAsync();
        await SaveAsync(options, Alert());

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(options, Alert()));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)duplicate.InnerException!).SqlState);
    }

    [Fact]
    public async Task AnotherRuleVersionOrAnotherListingMayAlertForTheSameSecond()
    {
        var options = await NewDatabaseAsync();
        await SaveAsync(options, Alert());

        await SaveAsync(options, Alert(version: "s30-0.005-z6-v1"));
        await SaveAsync(options, Alert(listing: 2));

        await using var context = new ResearchDbContext(options);
        Assert.Equal(3, await context.ShockAlerts.CountAsync(Ct));
    }
}
