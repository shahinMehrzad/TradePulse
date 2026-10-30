using Application.Commands.Auth;
using Application.Dtos.Auth;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

/// <summary>The order of the sign-in: nobody gets a token before the password is changed and two-factor is on and entered.</summary>
public class SignInHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly AuthenticatedUser User = new(UserId, "admin", "admin@example.com", ["Administrator"]);

    private sealed class FakeIdentity : IIdentityService
    {
        public Account? Account { get; set; } = new(User, MustChangePassword: false, TwoFactorEnabled: true);
        public CredentialsOutcome Credentials { get; set; } = CredentialsOutcome.Valid;
        public TwoFactorOutcome TwoFactor { get; set; } = TwoFactorOutcome.Valid;
        public PasswordChangeResult PasswordChange { get; set; } = new(true, []);
        public int PasswordChanges { get; private set; }

        public Task<CredentialsResult> CheckCredentialsAsync(string email, string username, string password, CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialsResult(Credentials, Credentials == CredentialsOutcome.Valid ? Account : null));

        public Task<Account?> FindActiveAccountAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(Account);

        public Task<TwoFactorOutcome> VerifyTwoFactorAsync(Guid userId, string code, TwoFactorMethod method, CancellationToken cancellationToken) =>
            Task.FromResult(TwoFactor);

        public Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
        {
            PasswordChanges++;
            if (PasswordChange.Succeeded)
                Account = Account! with { MustChangePassword = false };
            return Task.FromResult(PasswordChange);
        }

        public Task<EmailCode?> CreateEmailCodeAsync(Guid userId, bool forEnabling, CancellationToken cancellationToken) =>
            Task.FromResult<EmailCode?>(new("admin@example.com", "123456"));

        public Task<bool> EnableEmailCodesAsync(Guid userId, string code, CancellationToken cancellationToken) => Task.FromResult(code == "123456");

        public Task<bool> DisableEmailCodesAsync(Guid userId, string password, CancellationToken cancellationToken) => Task.FromResult(password == "right");

        public Task<int> CountRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(7);

        public Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid userId, string password, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>?>(password == "right" ? ["n-1", "n-2"] : null);

        public Task<TwoFactorEnrollment?> BeginTwoFactorEnrollmentAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<TwoFactorEnrollment?>(new("KEY", "otpauth://totp/x"));

        public Task<IReadOnlyList<string>?> ConfirmTwoFactorEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>?>(code == "123456" ? ["a-b"] : null);
    }

    private sealed class FakeChallenges : IChallengeTokenService
    {
        public ChallengeToken Create(Guid userId) => new("challenge-" + userId, DateTime.UtcNow.AddMinutes(10));
        public Guid? Validate(string token) => token == "challenge-" + UserId ? UserId : null;
    }

    private sealed class FakeMail : IMailSender
    {
        public bool IsConfigured { get; set; } = true;
        public List<(string To, string Body)> Sent { get; } = [];
        public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
        {
            Sent.Add((to, body));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccessTokens : IAccessTokenService
    {
        public AccessToken Create(AuthenticatedUser user) => new("access-" + user.UserName, DateTime.UtcNow.AddMinutes(30), 7);
    }

    private sealed class FakeRefreshTokens : IRefreshTokenRepository
    {
        public List<Guid> RevokedUsers { get; } = [];
        public Task<Guid?> ConsumeAsync(string tokenHash, string deviceId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(UserId);
        public Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default, string? exceptDeviceId = null)
        {
            RevokedUsers.Add(userId);
            return Task.CompletedTask;
        }
        public Task CleanUpExpiredTokensAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAuditLog : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task Add(AuditLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class Anonymous : ICurrentUser
    {
        public string Name => "unknown";
    }

    private readonly FakeIdentity _identity = new();
    private readonly FakeRefreshTokens _refreshTokens = new();
    private readonly FakeAuditLog _auditLog = new();

    private readonly FakeMail _mail = new();

    private LoginSteps Steps => new(new FakeAccessTokens(), new FakeChallenges(), _mail);

    private AuditTrail Audit => new(_auditLog, new Anonymous(), new FakeTimeProvider(), NullLogger<AuditTrail>.Instance);

    private Task<LoginStepDto> LoginAsync() =>
        new LoginCommandHandler(_identity, Steps).HandleAsync(new LoginCommand("admin@example.com", "", "pw"), Ct);

    [Theory]
    [InlineData(CredentialsOutcome.Invalid)]
    [InlineData(CredentialsOutcome.LockedOut)]
    public async Task AWrongOrLockedAccountAnswersTheSameWay(CredentialsOutcome outcome)
    {
        _identity.Credentials = outcome;

        var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(LoginAsync);

        Assert.Equal("The email/username or password is incorrect.", failure.Message);
    }

    [Fact]
    public async Task AnAccountWithEverythingInPlaceIsAskedForTheCode()
    {
        var step = await LoginAsync();

        Assert.Equal(LoginStage.TwoFactorRequired, step.Stage);
        Assert.Null(step.Session);
        Assert.NotNull(step.ChallengeToken);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task AnAccountThatMustChangeThePasswordOrEnrolGetsNoSession(bool mustChange, bool twoFactorOn)
    {
        _identity.Account = new Account(User, mustChange, twoFactorOn);

        var step = await LoginAsync();

        Assert.Equal(LoginStage.SetupRequired, step.Stage);
        Assert.Null(step.Session);
        Assert.Equal(mustChange, step.MustChangePassword);
        Assert.Equal(!twoFactorOn, step.NeedsTwoFactorSetup);
    }

    [Fact]
    public async Task ARightCodeOpensTheSession()
    {
        var step = await new VerifyTwoFactorCommandHandler(_identity, Steps)
            .HandleAsync(new VerifyTwoFactorCommand("challenge-" + UserId, "123456", TwoFactorMethod.Authenticator), Ct);

        Assert.Equal(LoginStage.Authenticated, step.Stage);
        Assert.Equal("access-admin", step.Session!.AccessToken);
    }

    [Theory]
    [InlineData(TwoFactorOutcome.Invalid, "The code is incorrect.")]
    [InlineData(TwoFactorOutcome.LockedOut, "Too many wrong codes. Try again later.")]
    public async Task AWrongCodeOpensNothing(TwoFactorOutcome outcome, string message)
    {
        _identity.TwoFactor = outcome;

        var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new VerifyTwoFactorCommandHandler(_identity, Steps)
            .HandleAsync(new VerifyTwoFactorCommand("challenge-" + UserId, "000000", TwoFactorMethod.Authenticator), Ct));

        Assert.Equal(message, failure.Message);
    }

    [Fact]
    public async Task TheCodeStepCannotBeUsedToSkipTheSetup()
    {
        _identity.Account = new Account(User, MustChangePassword: true, TwoFactorEnabled: true);
        _identity.TwoFactor = TwoFactorOutcome.Valid; // even a right code

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new VerifyTwoFactorCommandHandler(_identity, Steps)
            .HandleAsync(new VerifyTwoFactorCommand("challenge-" + UserId, "123456", TwoFactorMethod.Authenticator), Ct));
    }

    [Fact]
    public async Task ATokenThatIsNotAChallengeIsRefusedByEveryStep()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new VerifyTwoFactorCommandHandler(_identity, Steps)
            .HandleAsync(new VerifyTwoFactorCommand("garbage", "123456", TwoFactorMethod.Authenticator), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new BeginTwoFactorEnrollmentCommandHandler(_identity, Steps)
            .HandleAsync(new BeginTwoFactorEnrollmentCommand("garbage"), Ct));
    }

    [Fact]
    public async Task ChangingThePasswordEndsOldSessionsAndLeadsToTheNextStep()
    {
        _identity.Account = new Account(User, MustChangePassword: true, TwoFactorEnabled: false);

        var step = await new ChangeInitialPasswordCommandHandler(_identity, _refreshTokens, Steps, Audit)
            .HandleAsync(new ChangeInitialPasswordCommand("challenge-" + UserId, "old", "new"), Ct);

        Assert.Equal(LoginStage.SetupRequired, step.Stage);
        Assert.False(step.MustChangePassword);
        Assert.True(step.NeedsTwoFactorSetup);
        Assert.Equal([UserId], _refreshTokens.RevokedUsers);
        Assert.Equal("admin", Assert.Single(_auditLog.Entries).UserName); // the user themself, not "unknown"
    }

    [Fact]
    public async Task ARefusedPasswordIsReportedAndNothingIsRevoked()
    {
        _identity.Account = new Account(User, MustChangePassword: true, TwoFactorEnabled: false);
        _identity.PasswordChange = new PasswordChangeResult(false, ["Passwords must have at least 12 characters."]);

        var failure = await Assert.ThrowsAsync<ConfigurationValidationException>(() =>
            new ChangeInitialPasswordCommandHandler(_identity, _refreshTokens, Steps, Audit)
                .HandleAsync(new ChangeInitialPasswordCommand("challenge-" + UserId, "old", "short"), Ct));

        Assert.Contains("12 characters", failure.Message);
        Assert.Empty(_refreshTokens.RevokedUsers);
    }

    [Fact]
    public async Task ThePasswordCannotBeChangedToItself()
    {
        _identity.Account = new Account(User, MustChangePassword: true, TwoFactorEnabled: false);

        await Assert.ThrowsAsync<ConfigurationValidationException>(() =>
            new ChangeInitialPasswordCommandHandler(_identity, _refreshTokens, Steps, Audit)
                .HandleAsync(new ChangeInitialPasswordCommand("challenge-" + UserId, "same", "same"), Ct));

        Assert.Equal(0, _identity.PasswordChanges);
    }

    [Fact]
    public async Task TwoFactorCannotBeEnrolledBeforeThePasswordIsChanged()
    {
        _identity.Account = new Account(User, MustChangePassword: true, TwoFactorEnabled: false);

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => new BeginTwoFactorEnrollmentCommandHandler(_identity, Steps)
            .HandleAsync(new BeginTwoFactorEnrollmentCommand("challenge-" + UserId), Ct));
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => new ConfirmTwoFactorEnrollmentCommandHandler(_identity, Steps, Audit)
            .HandleAsync(new ConfirmTwoFactorEnrollmentCommand("challenge-" + UserId, "123456"), Ct));
    }

    [Fact]
    public async Task ARightFirstCodeTurnsTwoFactorOnAndSignsTheUserIn()
    {
        _identity.Account = new Account(User, MustChangePassword: false, TwoFactorEnabled: false);

        var enrolled = await new ConfirmTwoFactorEnrollmentCommandHandler(_identity, Steps, Audit)
            .HandleAsync(new ConfirmTwoFactorEnrollmentCommand("challenge-" + UserId, "123456"), Ct);

        Assert.Equal("access-admin", enrolled.Session.AccessToken);
        Assert.Equal(["a-b"], enrolled.RecoveryCodes);
    }

    [Fact]
    public async Task AWrongFirstCodeLeavesTwoFactorOff()
    {
        _identity.Account = new Account(User, MustChangePassword: false, TwoFactorEnabled: false);

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => new ConfirmTwoFactorEnrollmentCommandHandler(_identity, Steps, Audit)
            .HandleAsync(new ConfirmTwoFactorEnrollmentCommand("challenge-" + UserId, "000000"), Ct));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ARefreshTokenGivesNoSessionToAUserWhoMustSetUpAgain(bool mustChange, bool twoFactorOn)
    {
        _identity.Account = new Account(User, mustChange, twoFactorOn);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new ValidateRefreshTokenCommandHandler(_refreshTokens, _identity, new FakeAccessTokens())
                .HandleAsync(new ValidateRefreshTokenCommand("token", "device"), Ct));
    }

    [Fact]
    public async Task ARefreshTokenGivesNoSessionToADisabledUser()
    {
        _identity.Account = null;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new ValidateRefreshTokenCommandHandler(_refreshTokens, _identity, new FakeAccessTokens())
                .HandleAsync(new ValidateRefreshTokenCommand("token", "device"), Ct));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)] // the user did not turn the codes on
    [InlineData(false, true, false)] // the server has no mail settings
    public async Task TheCodeStepOffersEmailOnlyWhenTheUserAndTheServerAllowIt(bool userEnabled, bool mailConfigured, bool offered)
    {
        _identity.Account = new Account(User, MustChangePassword: false, TwoFactorEnabled: true, EmailCodesEnabled: userEnabled);
        _mail.IsConfigured = mailConfigured;

        var step = await LoginAsync();

        Assert.Equal(LoginStage.TwoFactorRequired, step.Stage);
        Assert.Equal(offered, step.EmailCodeAvailable);
    }

    [Fact]
    public async Task AnEmailCodeIsSentOnlyAfterTheSetupIsDone()
    {
        _identity.Account = new Account(User, MustChangePassword: true, TwoFactorEnabled: true, EmailCodesEnabled: true);
        var sender = new EmailCodeSender(_identity, _mail, new EmailCodeThrottle(new FakeTimeProvider()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SendEmailCodeCommandHandler(_identity, Steps, sender)
            .HandleAsync(new SendEmailCodeCommand("challenge-" + UserId), Ct));

        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task TheMailedCodeGoesToTheUsersAddressAndAskingAgainRightAwayIsRefused()
    {
        _identity.Account = new Account(User, MustChangePassword: false, TwoFactorEnabled: true, EmailCodesEnabled: true);
        var sender = new EmailCodeSender(_identity, _mail, new EmailCodeThrottle(new FakeTimeProvider()));
        var handler = new SendEmailCodeCommandHandler(_identity, Steps, sender);

        await handler.HandleAsync(new SendEmailCodeCommand("challenge-" + UserId), Ct);

        var mail = Assert.Single(_mail.Sent);
        Assert.Equal("admin@example.com", mail.To);
        Assert.Contains("123456", mail.Body);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => handler.HandleAsync(new SendEmailCodeCommand("challenge-" + UserId), Ct));
        Assert.Single(_mail.Sent);
    }

    [Fact]
    public async Task WithoutMailSettingsNothingIsSent()
    {
        _mail.IsConfigured = false;
        var sender = new EmailCodeSender(_identity, _mail, new EmailCodeThrottle(new FakeTimeProvider()));

        await Assert.ThrowsAsync<ConfigurationConflictException>(() => sender.SendAsync(UserId, forEnabling: true, Ct));

        Assert.Empty(_mail.Sent);
    }
}
