using Application.Interfaces;
using Infrastructure.Configurations;
using Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.UnitTests;

public class ChallengeTokenServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private static IOptions<JwtConfigurations> Options(string secret = "challenge-test-secret-challenge-test-secret-0123") =>
        Microsoft.Extensions.Options.Options.Create(new JwtConfigurations { Secret = secret, Issuer = "issuer", Audience = "audience", DurationInMinutes = 30 });

    private JwtChallengeTokenService Service(string secret = "challenge-test-secret-challenge-test-secret-0123") => new(Options(secret), _time);

    [Fact]
    public void ATokenNamesTheUserItWasIssuedTo()
    {
        var token = Service().Create(UserId);

        Assert.Equal(UserId, Service().Validate(token.Token));
    }

    [Fact]
    public void ATokenExpiresAfterItsLifetime()
    {
        var token = Service().Create(UserId);

        _time.Advance(JwtChallengeTokenService.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(UserId, Service().Validate(token.Token));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(Service().Validate(token.Token));
    }

    [Fact]
    public void ATokenSignedWithAnotherKeyIsRefused()
    {
        var forged = Service("another-secret-another-secret-another-secret-01").Create(UserId);

        Assert.Null(Service().Validate(forged.Token));
    }

    [Fact]
    public void AnAccessTokenIsNotAChallengeToken()
    {
        var access = new JwtAccessTokenService(Options()).Create(new AuthenticatedUser(UserId, "admin", "admin@example.com", ["Administrator"]));

        Assert.Null(Service().Validate(access.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    public void GarbageIsRefusedWithoutThrowing(string token) => Assert.Null(Service().Validate(token));
}
