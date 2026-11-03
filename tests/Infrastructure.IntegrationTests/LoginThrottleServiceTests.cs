using Application.Services;
using Dapper;
using Domain.Entities;
using Infrastructure.Identity;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// How much wrong guessing is allowed and how long it is locked for, on a real Postgres with a clock the tests move. The numbers are the rule
/// (<see cref="LoginThrottlePolicy"/>); what these tests show is that the rule is what happens, round after round, and that nothing is permanent.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LoginThrottleServiceTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Alice = Guid.NewGuid();
    private const string Home = "203.0.113.7";
    private const string Elsewhere = "198.51.100.9";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    private async Task<DbContextOptions<AuthDbContext>> NewDatabaseAsync()
    {
        var database = $"throttle_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
            await admin.ExecuteAsync($"CREATE DATABASE {database}");

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database, Pooling = false }.ConnectionString; // one pool per test database would use up the server
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "auth")).Options;
        await using var context = new AuthDbContext(options);
        await context.Database.MigrateAsync(Ct);
        return options;
    }

    private LoginThrottleService Service(DbContextOptions<AuthDbContext> options) => new(new AuthDbContext(options), _time);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task<Application.Interfaces.PasswordFailure> WrongPasswordsAsync(LoginThrottleService service, int count, string address = Home)
    {
        Application.Interfaces.PasswordFailure? last = null;
        for (var i = 0; i < count; i++)
            last = await service.PasswordFailedAsync(Alice, address, Ct);
        return last!;
    }

    // ---- passwords, one address

    [Fact]
    public async Task FourWrongPasswordsLockNothingAndTheFifthLocksTheAddress()
    {
        var service = Service(await NewDatabaseAsync());

        var four = await WrongPasswordsAsync(service, 4);
        Assert.False(four.FromAddress.JustLocked);
        Assert.Null(await service.PasswordLockedUntilAsync(Alice, Home, Ct));

        var fifth = await service.PasswordFailedAsync(Alice, Home, Ct);

        Assert.True(fifth.FromAddress.JustLocked);
        Assert.Equal(Now + TimeSpan.FromMinutes(5), fifth.FromAddress.LockedUntil);
        Assert.Equal(Now + TimeSpan.FromMinutes(5), await service.PasswordLockedUntilAsync(Alice, Home, Ct));
    }

    [Fact]
    public async Task ALockEndsByItself()
    {
        var service = Service(await NewDatabaseAsync());
        await WrongPasswordsAsync(service, 5);

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.NotNull(await service.PasswordLockedUntilAsync(Alice, Home, Ct));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await service.PasswordLockedUntilAsync(Alice, Home, Ct));
    }

    [Fact]
    public async Task EveryRoundLocksForLongerUntilTheLongestOne()
    {
        var service = Service(await NewDatabaseAsync());
        TimeSpan[] expected = [.. LoginThrottlePolicy.PasswordLocks, LoginThrottlePolicy.PasswordLocks[^1]]; // a sixth round is as long as the fifth

        for (var round = 1; round <= expected.Length; round++)
        {
            var failure = await WrongPasswordsAsync(service, 5);

            Assert.True(failure.FromAddress.JustLocked);
            Assert.Equal(round, failure.FromAddress.Round);
            Assert.Equal(Now + expected[round - 1], failure.FromAddress.LockedUntil);
            _time.Advance(expected[round - 1] + TimeSpan.FromSeconds(1));
        }

        // 5, 30 minutes, 2, 6 and 24 hours: and none of it is a lock that waits for a person.
        Assert.Equal([5, 30, 120, 360, 1440, 1440], expected.Select(e => (int)e.TotalMinutes));
    }

    [Fact]
    public async Task ARightPasswordMakesTheAddressStartFromTheShortestLockAgain()
    {
        var service = Service(await NewDatabaseAsync());
        await WrongPasswordsAsync(service, 5);
        _time.Advance(TimeSpan.FromMinutes(6));

        await service.PasswordSucceededAsync(Alice, Home, Ct);
        var again = await WrongPasswordsAsync(service, 5);

        Assert.Equal(1, again.FromAddress.Round);
        Assert.Equal(Now + TimeSpan.FromMinutes(5), again.FromAddress.LockedUntil);
    }

    [Fact]
    public async Task WrongPasswordsBeforeARightOneDoNotAddUpWithTheOnesAfter()
    {
        var service = Service(await NewDatabaseAsync());
        await WrongPasswordsAsync(service, 4);
        await service.PasswordSucceededAsync(Alice, Home, Ct);

        var after = await WrongPasswordsAsync(service, 4);

        Assert.False(after.FromAddress.JustLocked);
        Assert.Null(await service.PasswordLockedUntilAsync(Alice, Home, Ct));
    }

    [Fact]
    public async Task AnotherAddressIsNotLockedByThis()
    {
        var service = Service(await NewDatabaseAsync());

        await WrongPasswordsAsync(service, 5, Home);

        Assert.NotNull(await service.PasswordLockedUntilAsync(Alice, Home, Ct));
        Assert.Null(await service.PasswordLockedUntilAsync(Alice, Elsewhere, Ct));
    }

    [Fact]
    public async Task ADifferentAccountFromTheSameAddressIsNotLocked()
    {
        var service = Service(await NewDatabaseAsync());

        await WrongPasswordsAsync(service, 5, Home);

        Assert.Null(await service.PasswordLockedUntilAsync(Guid.NewGuid(), Home, Ct));
    }

    [Fact]
    public async Task AttemptsOlderThanADayAreForgotten()
    {
        var service = Service(await NewDatabaseAsync());
        await WrongPasswordsAsync(service, 4);

        _time.Advance(LoginThrottlePolicy.PasswordWindow + TimeSpan.FromMinutes(1));
        var fresh = await service.PasswordFailedAsync(Alice, Home, Ct);

        Assert.False(fresh.FromAddress.JustLocked);
        Assert.Equal(1, fresh.FromAddress.Failures);
    }

    [Fact]
    public async Task RoundsNobodyAddedToForAWeekAreForgotten()
    {
        var service = Service(await NewDatabaseAsync());
        await WrongPasswordsAsync(service, 5);   // round 1
        _time.Advance(TimeSpan.FromMinutes(6));
        await WrongPasswordsAsync(service, 5);   // round 2: 30 minutes
        _time.Advance(LoginThrottlePolicy.RoundsForgottenAfter + TimeSpan.FromHours(1));

        var again = await WrongPasswordsAsync(service, 5);

        Assert.Equal(1, again.FromAddress.Round);
        Assert.Equal(Now + TimeSpan.FromMinutes(5), again.FromAddress.LockedUntil);
    }

    // ---- passwords, many addresses

    [Fact]
    public async Task AGuessSpreadOverManyAddressesLocksTheAccountForAShortTime()
    {
        var service = Service(await NewDatabaseAsync());
        Application.Interfaces.PasswordFailure? last = null;
        for (var address = 0; address < 8; address++)
            for (var i = 0; i < 4 && (address * 4 + i) < LoginThrottlePolicy.AccountWideAttempts; i++)
                last = await service.PasswordFailedAsync(Alice, $"198.51.100.{address}", Ct);

        Assert.True(last!.AccountWide.JustLocked);
        Assert.Equal(Now + LoginThrottlePolicy.AccountWideLock, last.AccountWide.LockedUntil);
        Assert.NotNull(await service.PasswordLockedUntilAsync(Alice, "192.0.2.200", Ct)); // an address that never failed is closed too

        _time.Advance(LoginThrottlePolicy.AccountWideLock + TimeSpan.FromSeconds(1));
        Assert.Null(await service.PasswordLockedUntilAsync(Alice, "192.0.2.200", Ct));
    }

    // ---- two-factor codes

    [Fact]
    public async Task TheFifthWrongCodeLocksTheWholeAccountAndTheFirstFourOnlyCount()
    {
        var service = Service(await NewDatabaseAsync());

        var failures = new List<int>();
        for (var i = 0; i < 4; i++)
            failures.Add((await service.TwoFactorFailedAsync(Alice, Ct)).Failures);
        Assert.Equal([1, 2, 3, 4], failures);
        Assert.Null(await service.TwoFactorLockedUntilAsync(Alice, Ct));

        var fifth = await service.TwoFactorFailedAsync(Alice, Ct);

        Assert.True(fifth.JustLocked);
        Assert.Equal(Now + TimeSpan.FromMinutes(15), await service.TwoFactorLockedUntilAsync(Alice, Ct));
    }

    [Fact]
    public async Task EveryRoundOfWrongCodesLocksForLongerAndNeverForGood()
    {
        var service = Service(await NewDatabaseAsync());
        TimeSpan[] expected = [.. LoginThrottlePolicy.TwoFactorLocks, LoginThrottlePolicy.TwoFactorLocks[^1]];

        for (var round = 1; round <= expected.Length; round++)
        {
            Application.Interfaces.ThrottleOutcome outcome = null!;
            for (var i = 0; i < LoginThrottlePolicy.TwoFactorAttempts; i++)
                outcome = await service.TwoFactorFailedAsync(Alice, Ct);

            Assert.Equal(round, outcome.Round);
            Assert.Equal(Now + expected[round - 1], outcome.LockedUntil);
            _time.Advance(expected[round - 1] + TimeSpan.FromSeconds(1));
        }

        Assert.Equal([15, 60, 360, 1440, 1440], expected.Select(e => (int)e.TotalMinutes));
        Assert.Null(await service.TwoFactorLockedUntilAsync(Alice, Ct));
    }

    [Fact]
    public async Task ARightCodeStartsTheCountOver()
    {
        var service = Service(await NewDatabaseAsync());
        for (var i = 0; i < 4; i++)
            await service.TwoFactorFailedAsync(Alice, Ct);

        await service.TwoFactorSucceededAsync(Alice, Ct);

        Assert.Equal(1, (await service.TwoFactorFailedAsync(Alice, Ct)).Failures);
    }

    [Fact]
    public async Task PasswordsAndCodesAreCountedApart()
    {
        var service = Service(await NewDatabaseAsync());

        await WrongPasswordsAsync(service, 5);
        Assert.Null(await service.TwoFactorLockedUntilAsync(Alice, Ct));

        for (var i = 0; i < 5; i++)
            await service.TwoFactorFailedAsync(Alice, Ct);
        await service.PasswordSucceededAsync(Alice, Home, Ct);
        Assert.NotNull(await service.TwoFactorLockedUntilAsync(Alice, Ct)); // a right password does not lift a lock on the code
    }

    // ---- unlocking, listing, tidying

    [Fact]
    public async Task UnlockingForgetsEverythingOfTheUser()
    {
        var service = Service(await NewDatabaseAsync());
        await WrongPasswordsAsync(service, 5);
        for (var i = 0; i < 5; i++)
            await service.TwoFactorFailedAsync(Alice, Ct);

        await service.ClearAsync(Alice, Ct);

        Assert.Null(await service.PasswordLockedUntilAsync(Alice, Home, Ct));
        Assert.Null(await service.TwoFactorLockedUntilAsync(Alice, Ct));
        Assert.Equal(1, (await WrongPasswordsAsync(service, 1)).FromAddress.Failures);
        var code = await service.TwoFactorFailedAsync(Alice, Ct);
        Assert.Equal((1, 0), (code.Failures, code.Round));
    }

    [Fact]
    public async Task TheListOfLocksSaysWhichAccountsAreLockedAndHowManyAddressesAreOut()
    {
        var service = Service(await NewDatabaseAsync());
        var bob = Guid.NewGuid();
        await WrongPasswordsAsync(service, 5, Home);
        await WrongPasswordsAsync(service, 5, Elsewhere);
        for (var i = 0; i < 5; i++)
            await service.TwoFactorFailedAsync(bob, Ct);

        var locks = await service.ActiveLocksAsync(Ct);

        Assert.Equal(2, locks.Count);
        Assert.Equal((null, 2), (locks[Alice].LockedUntil, locks[Alice].LockedAddresses));
        Assert.Equal((Now + TimeSpan.FromMinutes(15), 0), (locks[bob].LockedUntil, locks[bob].LockedAddresses));

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await service.ActiveLocksAsync(Ct));
    }

    [Fact]
    public async Task OnlyWhatHasNothingLeftToSayIsTidiedAway()
    {
        var options = await NewDatabaseAsync();
        var service = Service(options);
        await WrongPasswordsAsync(service, 2, Home);          // counts only, no lock
        await WrongPasswordsAsync(service, 5, Elsewhere);     // a lock that will have ended

        _time.Advance(TimeSpan.FromDays(3));
        await service.CleanUpAsync(Ct);
        await using (var check = new AuthDbContext(options))
            Assert.Equal(2, await check.LoginThrottles.CountAsync(t => t.Kind == LoginThrottleKind.PasswordFromAddress, Ct));

        _time.Advance(TimeSpan.FromDays(7));
        await service.CleanUpAsync(Ct);
        await using (var check = new AuthDbContext(options))
            Assert.Empty(await check.LoginThrottles.ToListAsync(Ct));
    }

    // ---- at the same moment

    [Fact]
    public async Task WrongGuessesAtTheSameMomentAreCountedOneByOneAndLockOnce()
    {
        var options = await NewDatabaseAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await using var context = new AuthDbContext(options);
            return await new LoginThrottleService(context, _time).PasswordFailedAsync(Alice, Home, Ct);
        }, Ct)));

        Assert.Equal(1, outcomes.Count(o => o.FromAddress.JustLocked));       // none of them slipped under the limit, and it locked once
        await using var check = new AuthDbContext(options);
        var row = await check.LoginThrottles.SingleAsync(t => t.Kind == LoginThrottleKind.PasswordFromAddress, Ct);
        Assert.Equal(1, row.Rounds);
        Assert.Equal(Now + TimeSpan.FromMinutes(5), row.LockedUntil);
    }
}
