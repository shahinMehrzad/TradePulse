using Application.Interfaces;
using Application.Notifications;
using Application.Services;
using Domain.Entities;
using Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

/// <summary>When a wrong password or code is only counted, when it is written to the audit log, and when it sends a message.</summary>
public class LoginGuardTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeThrottle : ILoginThrottle
    {
        public DateTime? PasswordLock { get; set; }
        public DateTime? TwoFactorLock { get; set; }
        public PasswordFailure NextPasswordFailure { get; set; } = new(Quiet, Quiet);
        public ThrottleOutcome NextTwoFactorFailure { get; set; } = Quiet;
        public List<string> Calls { get; } = [];

        public static ThrottleOutcome Quiet => new(1, 0, null, false);

        public Task<DateTime?> PasswordLockedUntilAsync(Guid userId, string address, CancellationToken ct) => Task.FromResult(PasswordLock);
        public Task<PasswordFailure> PasswordFailedAsync(Guid userId, string address, CancellationToken ct) => Task.FromResult(NextPasswordFailure);
        public Task PasswordSucceededAsync(Guid userId, string address, CancellationToken ct) { Calls.Add($"password-ok {address}"); return Task.CompletedTask; }
        public Task<DateTime?> TwoFactorLockedUntilAsync(Guid userId, CancellationToken ct) => Task.FromResult(TwoFactorLock);
        public Task<ThrottleOutcome> TwoFactorFailedAsync(Guid userId, CancellationToken ct) => Task.FromResult(NextTwoFactorFailure);
        public Task TwoFactorSucceededAsync(Guid userId, CancellationToken ct) { Calls.Add("code-ok"); return Task.CompletedTask; }
        public Task ClearAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<Guid, AccountLock>> ActiveLocksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<Guid, AccountLock>>(new Dictionary<Guid, AccountLock>());
        public Task CleanUpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAlerts : ISecurityAlerts
    {
        public List<SecurityAlert> Raised { get; } = [];
        public void Raise(SecurityAlert alert) => Raised.Add(alert);
    }

    private sealed class FakeAuditLog : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task Add(AuditLogEntry entry, CancellationToken cancellationToken) { Entries.Add(entry); return Task.CompletedTask; }
    }

    private sealed class Anonymous : ICurrentUser { public string Name => "unknown"; }

    private sealed class Visitor(string address) : IClientAddress { public string Address => address; }

    private readonly FakeThrottle _throttle = new();
    private readonly FakeAlerts _alerts = new();
    private readonly FakeAuditLog _audit = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    private LoginGuard Guard(string address = "203.0.113.7") => new(
        _throttle, _alerts, new AuditTrail(_audit, new Anonymous(), _time, NullLogger<AuditTrail>.Instance), new Visitor(address), _time);

    private DateTime In(TimeSpan span) => _time.GetUtcNow().UtcDateTime + span;

    // ---- two-factor codes

    [Fact]
    public async Task TheFirstWrongCodeIsOnlyCounted()
    {
        _throttle.NextTwoFactorFailure = new ThrottleOutcome(1, 0, null, false);

        var locked = await Guard().TwoFactorFailedAsync(UserId, "alice", Ct);

        Assert.False(locked);
        Assert.Empty(_alerts.Raised);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task TheSecondWrongCodeInARowSendsAMessage()
    {
        _throttle.NextTwoFactorFailure = new ThrottleOutcome(2, 0, null, false);

        var locked = await Guard("198.51.100.4").TwoFactorFailedAsync(UserId, "alice", Ct);

        Assert.False(locked);
        var alert = Assert.Single(_alerts.Raised);
        Assert.Contains("alice", alert.Title);
        Assert.Contains("198.51.100.4", alert.Body);
        Assert.Contains("password", alert.Body);
    }

    [Fact]
    public async Task TheThirdAndFourthDoNotSendAnotherMessage()
    {
        foreach (var failures in new[] { 3, 4 })
        {
            _throttle.NextTwoFactorFailure = new ThrottleOutcome(failures, 0, null, false);
            await Guard().TwoFactorFailedAsync(UserId, "alice", Ct);
        }

        Assert.Empty(_alerts.Raised);
    }

    [Fact]
    public async Task TheCodeThatClosesTheLockWritesTheAuditLogAndSendsAMessage()
    {
        _throttle.NextTwoFactorFailure = new ThrottleOutcome(0, 1, In(TimeSpan.FromMinutes(15)), true);

        var locked = await Guard().TwoFactorFailedAsync(UserId, "alice", Ct);

        Assert.True(locked);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(("system", "alice"), (entry.UserName, entry.Summary));
        Assert.Contains("15 minutes", entry.Details);
        var alert = Assert.Single(_alerts.Raised);
        Assert.Contains("locked", alert.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-11-03 12:15 UTC", alert.Body);
    }

    [Fact]
    public async Task ARightCodeOnlyClearsTheCount()
    {
        await Guard().TwoFactorSucceededAsync(UserId, Ct);

        Assert.Equal(["code-ok"], _throttle.Calls);
        Assert.Empty(_alerts.Raised);
    }

    // ---- passwords

    [Fact]
    public async Task AWrongPasswordIsOnlyCounted()
    {
        await Guard().PasswordFailedAsync(UserId, "alice", Ct);

        Assert.Empty(_alerts.Raised);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task TheFirstLockOfAnAddressIsWrittenDownButSendsNoMessage()
    {
        // Most often a person who typed it wrong five times.
        _throttle.NextPasswordFailure = new PasswordFailure(new ThrottleOutcome(0, 1, In(TimeSpan.FromMinutes(5)), true), FakeThrottle.Quiet);

        await Guard("203.0.113.9").PasswordFailedAsync(UserId, "alice", Ct);

        var entry = Assert.Single(_audit.Entries);
        Assert.Contains("203.0.113.9", entry.Details);
        Assert.Contains("round 1", entry.Details);
        Assert.Empty(_alerts.Raised);
    }

    [Fact]
    public async Task FromTheSecondRoundOnTheLockSendsAMessage()
    {
        _throttle.NextPasswordFailure = new PasswordFailure(new ThrottleOutcome(0, 2, In(TimeSpan.FromMinutes(30)), true), FakeThrottle.Quiet);

        await Guard("203.0.113.9").PasswordFailedAsync(UserId, "alice", Ct);

        var alert = Assert.Single(_alerts.Raised);
        Assert.Contains("round 2", alert.Body);
        Assert.Contains("203.0.113.9", alert.Body);
    }

    [Fact]
    public async Task AGuessFromManyAddressesLocksTheAccountAndSendsAMessage()
    {
        _throttle.NextPasswordFailure = new PasswordFailure(FakeThrottle.Quiet, new ThrottleOutcome(0, 1, In(TimeSpan.FromMinutes(15)), true));

        await Guard().PasswordFailedAsync(UserId, "alice", Ct);

        Assert.Single(_audit.Entries);
        var alert = Assert.Single(_alerts.Raised);
        Assert.Contains("several addresses", alert.Body);
    }

    [Fact]
    public async Task AnAddressIsLockedOutWhileTheThrottleSaysSo()
    {
        _throttle.PasswordLock = In(TimeSpan.FromMinutes(5));

        Assert.True(await Guard().PasswordLockedAsync(UserId, Ct));

        _throttle.PasswordLock = null;
        Assert.False(await Guard().PasswordLockedAsync(UserId, Ct));
    }

    [Fact]
    public async Task NothingSentAsAMessageContainsAPasswordOrACode()
    {
        _throttle.NextTwoFactorFailure = new ThrottleOutcome(2, 0, null, false);
        await Guard().TwoFactorFailedAsync(UserId, "alice", Ct);
        _throttle.NextTwoFactorFailure = new ThrottleOutcome(0, 1, In(TimeSpan.FromMinutes(15)), true);
        await Guard().TwoFactorFailedAsync(UserId, "alice", Ct);

        // The guard is never given what was typed, so there is nothing of it to leak: only who, from where and until when.
        Assert.All(_alerts.Raised, alert => Assert.DoesNotMatch(@"\b\d{6}\b", alert.Title + alert.Body));
    }
}
