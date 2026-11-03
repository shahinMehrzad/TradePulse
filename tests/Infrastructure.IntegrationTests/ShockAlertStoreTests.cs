using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>The store that keeps shock alerts, on a real Postgres with the real migration.</summary>
[Collection(PostgresCollection.Name)]
public class ShockAlertStoreTests(PostgresFixture postgres)
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

    private static ShockAlert Alert(
        int seconds = 0, AlertLevelEnum level = AlertLevelEnum.Attention, decimal move = -0.0031m, int flagged = 1, int listing = 1, string version = "s30-test-v1") => new()
    {
        SymbolListingId = listing,
        TriggeredAt = Moment.AddSeconds(seconds),
        Direction = move > 0 ? EventDirectionEnum.Up : EventDirectionEnum.Down,
        Level = level,
        WindowSeconds = 30,
        Move = move,
        ZScore = move > 0 ? 7.1m : -7.1m,
        StartPrice = 79893.4m,
        EndPrice = 79645.7m,
        Volume = 12.5m,
        FlaggedSeconds = flagged,
        RuleVersion = version,
    };

    private static async Task<List<ShockAlert>> AllAsync(DbContextOptions<ResearchDbContext> options)
    {
        await using var context = new ResearchDbContext(options);
        return await context.ShockAlerts.AsNoTracking().OrderBy(a => a.TriggeredAt).ThenBy(a => a.RuleVersion).ToListAsync(Ct);
    }

    [Fact]
    public async Task ANewAlertIsStored()
    {
        var options = await NewDatabaseAsync();

        await new ShockAlertStore(new ResearchDbContext(options)).SaveAsync(Alert(), Ct);

        var stored = Assert.Single(await AllAsync(options));
        Assert.Equal(Moment, stored.TriggeredAt);
        Assert.Equal((AlertLevelEnum.Attention, -0.0031m, 1), (stored.Level, stored.Move, stored.FlaggedSeconds));
    }

    [Fact]
    public async Task SavingTheSameAlertAgainUpdatesItWithoutAddingARow()
    {
        var options = await NewDatabaseAsync();
        var store = new ShockAlertStore(new ResearchDbContext(options));
        await store.SaveAsync(Alert(), Ct);
        var createdAt = (await AllAsync(options)).Single().CreateDate;

        // The alert ended: same start, now an alarm with a larger move and more flagged seconds.
        await store.SaveAsync(Alert(level: AlertLevelEnum.Alarm, move: -0.0074m, flagged: 5), Ct);

        var stored = Assert.Single(await AllAsync(options));
        Assert.Equal((AlertLevelEnum.Alarm, -0.0074m, 5), (stored.Level, stored.Move, stored.FlaggedSeconds));
        Assert.Equal(createdAt, stored.CreateDate);
    }

    [Fact]
    public async Task AnotherStartAnotherRuleVersionOrAnotherListingIsAnotherAlert()
    {
        var options = await NewDatabaseAsync();
        var store = new ShockAlertStore(new ResearchDbContext(options));

        await store.SaveAsync(Alert(), Ct);
        await store.SaveAsync(Alert(seconds: 600), Ct);
        await store.SaveAsync(Alert(version: "s30-test-v2"), Ct);
        await store.SaveAsync(Alert(listing: 2), Ct);

        Assert.Equal(4, (await AllAsync(options)).Count);
    }

    [Fact]
    public async Task OneContextKeptForDaysDoesNotCollectEverythingItEverSaved()
    {
        var options = await NewDatabaseAsync();
        await using var context = new ResearchDbContext(options);
        var store = new ShockAlertStore(context);

        for (var i = 0; i < 20; i++)
            await store.SaveAsync(Alert(seconds: i * 600), Ct);

        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(20, (await AllAsync(options)).Count);
    }

    [Fact]
    public async Task TimesOfEitherKindAreTheSameAlert()
    {
        var options = await NewDatabaseAsync();
        var store = new ShockAlertStore(new ResearchDbContext(options));
        await store.SaveAsync(Alert(), Ct);

        var unspecified = Alert(level: AlertLevelEnum.Alarm);
        unspecified.TriggeredAt = DateTime.SpecifyKind(unspecified.TriggeredAt, DateTimeKind.Unspecified);
        await store.SaveAsync(unspecified, Ct);

        var stored = Assert.Single(await AllAsync(options));
        Assert.Equal(AlertLevelEnum.Alarm, stored.Level);
    }
}
