using Application.Interfaces;
using Infrastructure.Configurations;
using Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.UnitTests;

public class ElevationTokenServiceTests
{
    private const string Secret = "elevation-test-secret-elevation-test-secret-0123";
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private static IOptions<JwtConfigurations> Options(string secret = Secret) =>
        Microsoft.Extensions.Options.Options.Create(new JwtConfigurations { Secret = secret, Issuer = "issuer", Audience = "audience", DurationInMinutes = 30 });

    private JwtElevationTokenService Elevation(string secret = Secret) => new(Options(secret), _time);

    [Fact]
    public void ATokenNamesTheUserItWasIssuedTo()
    {
        Assert.Equal(UserId, Elevation().Validate(Elevation().Create(UserId).Token));
    }

    [Fact]
    public void ATokenLastsTenMinutesAndAFreshOneIsNeededAfter()
    {
        var token = Elevation().Create(UserId);

        _time.Advance(JwtElevationTokenService.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(UserId, Elevation().Validate(token.Token));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(Elevation().Validate(token.Token));
    }

    [Fact]
    public void ATokenFromAnotherKeyIsRefused()
    {
        var forged = Elevation("another-secret-another-secret-another-secret-01").Create(UserId);

        Assert.Null(Elevation().Validate(forged.Token));
    }

    [Fact]
    public void ATokenOfAnotherPurposeIsNotAConfirmation()
    {
        var challenge = new JwtChallengeTokenService(Options(), _time).Create(UserId);
        var access = new JwtAccessTokenService(Options()).Create(new AuthenticatedUser(UserId, "admin", "admin@example.com", ["Administrator"]));

        Assert.Null(Elevation().Validate(challenge.Token));
        Assert.Null(Elevation().Validate(access.Token));
    }

    [Fact]
    public void AConfirmationIsNotAChallengeToken()
    {
        var elevation = Elevation().Create(UserId);

        Assert.Null(new JwtChallengeTokenService(Options(), _time).Validate(elevation.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public void MissingOrGarbageIsRefusedWithoutThrowing(string? token) => Assert.Null(Elevation().Validate(token));
}
