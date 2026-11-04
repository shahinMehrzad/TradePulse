using Dapper;
using Domain.Entities;
using Domain.Repositories;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// What presenting a refresh token comes to: a good one is used up once, one that was renewed and is shown again is a copy of a session (and is
/// told apart from one that was only signed out or expired), and a sign-out ends one device and no other.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RefreshTokenRepositoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Alice = Guid.NewGuid();

    private string _connectionString = "";

    private async Task<DbContextOptions<AuthDbContext>> NewDatabaseAsync()
    {
        var database = $"refreshrepo_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
            await admin.ExecuteAsync($"CREATE DATABASE {database}");

        _connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(_connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "auth")).Options;
        await using var context = new AuthDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    private static async Task SeedAsync(DbContextOptions<AuthDbContext> options, string hash, string device = "laptop", TimeSpan? expiresIn = null,
        bool revoked = false, TimeSpan? usedAgo = null, Guid? user = null)
    {
        await using var context = new AuthDbContext(options);
        context.RefreshTokens.Add(new RefreshToken
        {
            Token = hash,
            UserId = user ?? Alice,
            DeviceId = device,
            ExpiresOn = DateTime.UtcNow + (expiresIn ?? TimeSpan.FromDays(7)),
            IsRevoked = revoked || usedAgo is not null,
            UsedAt = usedAgo is { } ago ? DateTime.UtcNow - ago : null
        });
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<RefreshTokenUse> ConsumeAsync(DbContextOptions<AuthDbContext> options, string hash, string device = "laptop")
    {
        await using var context = new AuthDbContext(options);
        return await new RefreshTokenRepository(context).ConsumeAsync(hash, device, Ct);
    }

    [Fact]
    public async Task AGoodTokenIsUsedUpOnce()
    {
        var options = await NewDatabaseAsync();
        await SeedAsync(options, "t1");

        var first = await ConsumeAsync(options, "t1");
        var second = await ConsumeAsync(options, "t1");

        Assert.Equal(Alice, first.UserId);
        Assert.Null(second.UserId);
    }

    [Fact]
    public async Task ASecondUseRightAfterIsOnlyRefusedNotTakenForATheft()
    {
        var options = await NewDatabaseAsync();
        await SeedAsync(options, "t1");
        await ConsumeAsync(options, "t1");

        var again = await ConsumeAsync(options, "t1");   // an answer that was lost and asked for again, seconds later

        Assert.Equal(RefreshTokenUse.Nothing, again);
    }

    [Fact]
    public async Task ATokenThatWasRenewedLongAgoAndIsShownAgainIsReportedAsReused()
    {
        var options = await NewDatabaseAsync();
        await SeedAsync(options, "spent", device: "laptop", usedAgo: TimeSpan.FromMinutes(5));

        var use = await ConsumeAsync(options, "spent", device: "somewhere-else");   // the thief may name any device

        Assert.Null(use.UserId);
        Assert.Equal(Alice, use.ReusedByUserId);
        Assert.Equal("laptop", use.ReusedDeviceId);                                    // the device it was issued to, not the one that presented it
    }

    [Fact]
    public async Task ATokenThatWasSignedOutIsNotReportedAsReused()
    {
        var options = await NewDatabaseAsync();
        await SeedAsync(options, "ended", revoked: true);

        Assert.Equal(RefreshTokenUse.Nothing, await ConsumeAsync(options, "ended"));
    }

    [Fact]
    public async Task AnExpiredTokenIsNotReportedAsReusedEitherWhenItWasRenewed()
    {
        var options = await NewDatabaseAsync();
        await SeedAsync(options, "old", expiresIn: TimeSpan.FromHours(-1), usedAgo: TimeSpan.FromDays(6));

        Assert.Equal(RefreshTokenUse.Nothing, await ConsumeAsync(options, "old"));
    }

    [Fact]
    public async Task AnUnknownTokenGivesNothing()
    {
        var options = await NewDatabaseAsync();

        Assert.Equal(RefreshTokenUse.Nothing, await ConsumeAsync(options, "nobody"));
    }

    [Fact]
    public async Task ATokenIsGoodOnlyForTheDeviceItWasIssuedToAndStaysGoodThere()
    {
        var options = await NewDatabaseAsync();
        await SeedAsync(options, "t1", device: "laptop");

        Assert.Equal(RefreshTokenUse.Nothing, await ConsumeAsync(options, "t1", device: "phone"));
        Assert.Equal(Alice, (await ConsumeAsync(options, "t1", device: "laptop")).UserId);
    }

    [Fact]
    public async Task OfManyCallsWithTheSameTokenExactlyOneGetsTheUserAndNoneIsCalledATheft()
    {
        var options = await NewDatabaseAsync();
        await SeedAsync(options, "race");

        var uses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ConsumeAsync(options, "race"), Ct)));

        Assert.Equal(1, uses.Count(u => u.UserId is not null));
        Assert.DoesNotContain(uses, u => u.ReusedByUserId is not null);
    }

    [Fact]
    public async Task SigningOutEndsThatDeviceAndNoOtherDeviceOrUser()
    {
        var options = await NewDatabaseAsync();
        var bob = Guid.NewGuid();
        await SeedAsync(options, "a-laptop-1", device: "laptop");
        await SeedAsync(options, "a-laptop-2", device: "laptop");
        await SeedAsync(options, "a-phone", device: "phone");
        await SeedAsync(options, "b-laptop", device: "laptop", user: bob);

        await using (var context = new AuthDbContext(options))
            await new RefreshTokenRepository(context).RevokeDeviceAsync(Alice, "laptop", Ct);

        Assert.Null((await ConsumeAsync(options, "a-laptop-1")).UserId);
        Assert.Null((await ConsumeAsync(options, "a-laptop-2")).UserId);
        Assert.Equal(Alice, (await ConsumeAsync(options, "a-phone", "phone")).UserId);
        Assert.Equal(bob, (await ConsumeAsync(options, "b-laptop")).UserId);
    }

    [Fact]
    public async Task ASignedOutTokenStaysAPlainRefusalWhenShownAgain()
    {
        var options = await NewDatabaseAsync();
        await SeedAsync(options, "t1");
        await using (var context = new AuthDbContext(options))
            await new RefreshTokenRepository(context).RevokeDeviceAsync(Alice, "laptop", Ct);

        // Signed out, not renewed: nothing says that a copy exists, so it is not an alarm.
        Assert.Equal(RefreshTokenUse.Nothing, await ConsumeAsync(options, "t1"));
    }
}
