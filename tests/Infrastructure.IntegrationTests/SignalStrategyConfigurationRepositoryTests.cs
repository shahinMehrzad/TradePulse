using Dapper;
using Domain.Entities;
using Domain.Exceptions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>The strategies whose signals are taken, on a real Postgres with the real migrations.</summary>
[Collection(PostgresCollection.Name)]
public class SignalStrategyConfigurationRepositoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string _connectionString = string.Empty;

    private async Task<DbContextOptions<ConfigDbContext>> NewDatabaseAsync()
    {
        var database = $"strategies_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.ExecuteAsync($"CREATE DATABASE {database}");

        _connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "config"))
            .Options;

        await using var context = new ConfigDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    private static SignalStrategyConfigurationRepository NewRepository(DbContextOptions<ConfigDbContext> options) => new(new ConfigDbContext(options));

    private static SignalStrategyConfiguration S1() => new()
    {
        Strategy = "S1", IsEnabled = true, MaxSignalAgeSeconds = 3600, MaxMoveOfStopDistance = 0.25m, MaxOpenPerSymbol = null, MaxLeverage = 20m,
    };

    [Fact]
    public async Task ThereAreNoStrategiesUntilAnAdminAddsOne()
    {
        var repository = NewRepository(await NewDatabaseAsync());

        Assert.Empty(await repository.GetAll(Ct));
        Assert.Null(await repository.GetByStrategy("S1", Ct));
    }

    [Fact]
    public async Task AStrategyIsAddedAndFoundByItsLabel()
    {
        var repository = NewRepository(await NewDatabaseAsync());
        var configuration = S1();
        configuration.MaxOpenPerSymbol = 3;

        var id = await repository.Add(configuration, Ct);

        var found = await repository.GetByStrategy("S1", Ct);
        Assert.NotNull(found);
        Assert.Equal(id, found.Id);
        Assert.True(found.IsEnabled);
        Assert.Equal((3600, 0.25m, 3, 20m), (found.MaxSignalAgeSeconds, found.MaxMoveOfStopDistance, found.MaxOpenPerSymbol, found.MaxLeverage));
        Assert.True(found.CreateDate > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task TheLabelIsMatchedExactly()
    {
        var repository = NewRepository(await NewDatabaseAsync());
        await repository.Add(S1(), Ct);

        Assert.Null(await repository.GetByStrategy("s1", Ct));
        Assert.Null(await repository.GetByStrategy("S1 ", Ct));
        Assert.Null(await repository.GetByStrategy("S2", Ct));
    }

    [Fact]
    public async Task AStrategyWithNoLimitOfOpenTradesComesBackWithNone()
    {
        var repository = NewRepository(await NewDatabaseAsync());
        await repository.Add(S1(), Ct);

        Assert.Null((await repository.GetByStrategy("S1", Ct))!.MaxOpenPerSymbol);
    }

    [Fact]
    public async Task AStrategyCannotBeAddedTwice()
    {
        var options = await NewDatabaseAsync();
        await NewRepository(options).Add(S1(), Ct);

        await Assert.ThrowsAsync<ConfigurationConflictException>(() => NewRepository(options).Add(S1(), Ct));
    }

    [Fact]
    public async Task ValuesThatAreNotAllowedAreRefusedBeforeTheDatabaseIsAsked()
    {
        var repository = NewRepository(await NewDatabaseAsync());
        var bad = S1();
        bad.MaxMoveOfStopDistance = 2m;

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => repository.Add(bad, Ct));
        Assert.Empty(await repository.GetAll(Ct));
    }

    [Theory]
    [InlineData("0", "0.25", "NULL", "20")]
    [InlineData("3600", "0", "NULL", "20")]
    [InlineData("3600", "1.5", "NULL", "20")]
    [InlineData("3600", "0.25", "0", "20")]
    [InlineData("3600", "0.25", "NULL", "200")]
    public async Task TheDatabaseRefusesWhatTheApplicationWould(string age, string move, string open, string leverage)
    {
        await NewDatabaseAsync();
        await using var connection = new NpgsqlConnection(_connectionString);
        var sql = $"""
            INSERT INTO config.signal_strategy_configurations ("Strategy", "IsEnabled", "MaxSignalAgeSeconds", "MaxMoveOfStopDistance", "MaxOpenPerSymbol", "MaxLeverage")
            VALUES ('X', true, {age}, {move}, {open}, {leverage})
            """;

        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: Ct)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task TheLimitsAndTheSwitchAreChangedWithoutChangingTheName()
    {
        var options = await NewDatabaseAsync();
        var id = await NewRepository(options).Add(S1(), Ct);

        var changed = S1();
        changed.Id = id;
        changed.IsEnabled = false;
        changed.MaxSignalAgeSeconds = 120;
        changed.MaxMoveOfStopDistance = 0.1m;
        changed.MaxOpenPerSymbol = 1;
        changed.MaxLeverage = 10m;
        await NewRepository(options).Update(changed, Ct);

        var found = (await NewRepository(options).GetByStrategy("S1", Ct))!;
        Assert.False(found.IsEnabled);
        Assert.Equal((120, 0.1m, 1, 10m), (found.MaxSignalAgeSeconds, found.MaxMoveOfStopDistance, found.MaxOpenPerSymbol, found.MaxLeverage));
    }

    [Fact]
    public async Task AStrategyWithoutAnAgeLimitIsStoredAndReadBack()
    {
        var options = await NewDatabaseAsync();
        var configuration = S1();
        configuration.MaxSignalAgeSeconds = null;
        await NewRepository(options).Add(configuration, Ct);

        var found = (await NewRepository(options).GetByStrategy("S1", Ct))!;
        Assert.Null(found.MaxSignalAgeSeconds);
    }

    [Fact]
    public async Task TheNameOfAStrategyCannotBeChanged()
    {
        var options = await NewDatabaseAsync();
        var id = await NewRepository(options).Add(S1(), Ct);

        var renamed = S1();
        renamed.Id = id;
        renamed.Strategy = "S9";

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => NewRepository(options).Update(renamed, Ct));
        Assert.NotNull(await NewRepository(options).GetByStrategy("S1", Ct));
    }

    [Fact]
    public async Task ABadUpdateChangesNothing()
    {
        var options = await NewDatabaseAsync();
        var id = await NewRepository(options).Add(S1(), Ct);

        var bad = S1();
        bad.Id = id;
        bad.MaxLeverage = 500m;
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => NewRepository(options).Update(bad, Ct));

        Assert.Equal(20m, (await NewRepository(options).GetByStrategy("S1", Ct))!.MaxLeverage);
    }

    [Fact]
    public async Task UpdatingARowThatIsGoneIsReported()
    {
        var repository = NewRepository(await NewDatabaseAsync());
        var missing = S1();
        missing.Id = 999;

        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => repository.Update(missing, Ct));
        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => repository.SetEnabled(999, true, Ct));
    }

    [Fact]
    public async Task AStrategyIsSwitchedOffAndOnAndKeepsItsRow()
    {
        var options = await NewDatabaseAsync();
        var id = await NewRepository(options).Add(S1(), Ct);

        await NewRepository(options).SetEnabled(id, false, Ct);
        Assert.False((await NewRepository(options).GetByStrategy("S1", Ct))!.IsEnabled);

        await NewRepository(options).SetEnabled(id, true, Ct);
        Assert.True((await NewRepository(options).GetByStrategy("S1", Ct))!.IsEnabled);
    }

    [Fact]
    public async Task EveryStrategyIsListedByName()
    {
        var repository = NewRepository(await NewDatabaseAsync());
        var second = S1();
        second.Strategy = "S2";
        await repository.Add(second, Ct);
        await repository.Add(S1(), Ct);

        Assert.Equal(["S1", "S2"], (await repository.GetAll(Ct)).Select(c => c.Strategy));
    }
}
