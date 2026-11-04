using Dapper;
using Infrastructure.Identity;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>A two-factor code works once: the same code of the same user is refused the second time, for as long as it could still be accepted.</summary>
[Collection(PostgresCollection.Name)]
public class TwoFactorReplayGuardTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Alice = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    private async Task<DbContextOptions<AuthDbContext>> NewDatabaseAsync()
    {
        var database = $"replay_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
            await admin.ExecuteAsync($"CREATE DATABASE {database}");

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "auth")).Options;
        await using var context = new AuthDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    private async Task<bool> UseAsync(DbContextOptions<AuthDbContext> options, Guid user, string code)
    {
        await using var context = new AuthDbContext(options);
        return await new TwoFactorReplayGuard(context, _time).TryUseAsync(user, code, Ct);
    }

    [Fact]
    public async Task ACodeWorksTheFirstTimeAndNotTheSecond()
    {
        var options = await NewDatabaseAsync();

        Assert.True(await UseAsync(options, Alice, "123456"));
        Assert.False(await UseAsync(options, Alice, "123456"));
        Assert.False(await UseAsync(options, Alice, "123456"));
    }

    [Fact]
    public async Task AnotherCodeOrAnotherUserIsNotAffected()
    {
        var options = await NewDatabaseAsync();
        await UseAsync(options, Alice, "123456");

        Assert.True(await UseAsync(options, Alice, "654321"));
        Assert.True(await UseAsync(options, Guid.NewGuid(), "123456"));
    }

    [Fact]
    public async Task TheSameDigitsAreFineAgainWhenTheCodeCouldNotBeAcceptedAnyMore()
    {
        var options = await NewDatabaseAsync();
        await UseAsync(options, Alice, "123456");

        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.True(await UseAsync(options, Alice, "123456"));
    }

    [Fact]
    public async Task OfManyUsesAtTheSameMomentExactlyOneWorks()
    {
        var options = await NewDatabaseAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => UseAsync(options, Alice, "777777"), Ct)));

        Assert.Equal(1, results.Count(worked => worked));
    }

    [Fact]
    public async Task TheCodeItselfIsNotWhatIsStored()
    {
        var options = await NewDatabaseAsync();
        await UseAsync(options, Alice, "424242");

        await using var context = new AuthDbContext(options);
        var stored = await context.UsedTwoFactorCodes.SingleAsync(Ct);

        Assert.Equal(Alice, stored.UserId);
        Assert.DoesNotContain("424242", stored.CodeHash);
        Assert.Equal(64, stored.CodeHash.Length);
    }
}
