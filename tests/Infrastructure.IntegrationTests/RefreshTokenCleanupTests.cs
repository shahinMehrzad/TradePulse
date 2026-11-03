using Dapper;
using Domain.Entities;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// Every sign-in and every renewal of a session leaves a refresh token behind. The ones that ran out go; the ones that were used up
/// (revoked) but have not expired stay, because they are what says that a token was already spent.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RefreshTokenCleanupTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<DbContextOptions<AuthDbContext>> NewDatabaseAsync()
    {
        var database = $"refresh_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
            await admin.ExecuteAsync($"CREATE DATABASE {database}");

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "auth")).Options;
        await using var context = new AuthDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    private static RefreshToken Token(string name, TimeSpan expiresIn, bool revoked) =>
        new() { Token = name, UserId = Guid.NewGuid(), DeviceId = "d", ExpiresOn = DateTime.UtcNow + expiresIn, IsRevoked = revoked };

    [Fact]
    public async Task OnlyTheTokensThatRanOutAreRemoved()
    {
        var options = await NewDatabaseAsync();
        await using (var seed = new AuthDbContext(options))
        {
            seed.RefreshTokens.AddRange(
                Token("expired", TimeSpan.FromHours(-1), revoked: false),
                Token("expired-and-spent", TimeSpan.FromHours(-1), revoked: true),
                Token("live", TimeSpan.FromDays(3), revoked: false),
                Token("spent-not-expired", TimeSpan.FromDays(3), revoked: true));
            await seed.SaveChangesAsync(Ct);
        }

        await using (var context = new AuthDbContext(options))
            await new RefreshTokenRepository(context).CleanUpExpiredTokensAsync(Ct);

        await using var check = new AuthDbContext(options);
        Assert.Equal(["live", "spent-not-expired"], (await check.RefreshTokens.Select(t => t.Token).ToListAsync(Ct)).Order());
    }

    [Fact]
    public async Task CleaningAnEmptyTableIsFine()
    {
        var options = await NewDatabaseAsync();

        await using var context = new AuthDbContext(options);
        await new RefreshTokenRepository(context).CleanUpExpiredTokensAsync(Ct);

        Assert.Empty(await context.RefreshTokens.ToListAsync(Ct));
    }
}
