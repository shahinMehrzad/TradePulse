using Application.Interfaces;
using Infrastructure.Configurations;
using Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tests.Shared;

namespace Infrastructure.UnitTests;

public class ChallengeTokenServiceTests
{
    private static readonly JwtTestKeys Main = JwtTestKeys.New();
    private static readonly JwtTestKeys Other = JwtTestKeys.New();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private static IOptions<JwtConfigurations> Options(JwtTestKeys? keys = null) =>
        Microsoft.Extensions.Options.Options.Create(new JwtConfigurations { PrivateKey = (keys ?? Main).PrivateKey, Issuer = "issuer", Audience = "audience", DurationInMinutes = 30 });

    private JwtChallengeTokenService Service(JwtTestKeys? keys = null) => new(Options(keys), _time);

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
        var forged = Service(Other).Create(UserId);

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
