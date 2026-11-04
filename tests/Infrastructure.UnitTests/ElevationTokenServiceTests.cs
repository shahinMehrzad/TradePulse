using Application.Interfaces;
using Infrastructure.Configurations;
using Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tests.Shared;

namespace Infrastructure.UnitTests;

public class ElevationTokenServiceTests
{
    private static readonly JwtTestKeys Main = JwtTestKeys.New();
    private static readonly JwtTestKeys Other = JwtTestKeys.New();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private static IOptions<JwtConfigurations> Options(JwtTestKeys? keys = null) =>
        Microsoft.Extensions.Options.Options.Create(new JwtConfigurations { PrivateKey = (keys ?? Main).PrivateKey, Issuer = "issuer", Audience = "audience", DurationInMinutes = 30 });

    /// <summary>What the key vault has: the public key only.</summary>
    private static IOptions<JwtConfigurations> PublicOnly(JwtTestKeys? keys = null) =>
        Microsoft.Extensions.Options.Options.Create(new JwtConfigurations { PublicKey = (keys ?? Main).PublicKey, Issuer = "issuer", Audience = "audience" });

    private JwtElevationTokenService Elevation(JwtTestKeys? keys = null) => new(Options(keys), _time);

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
        var forged = Elevation(Other).Create(UserId);

        Assert.Null(Elevation().Validate(forged.Token));
    }

    [Fact]
    public void AProgramWithOnlyThePublicKeyChecksTheTokenAndCannotMakeOne()
    {
        var made = Elevation().Create(UserId);
        var vault = new JwtElevationTokenService(PublicOnly(), _time);

        Assert.Equal(UserId, vault.Validate(made.Token));
        Assert.Throws<InvalidOperationException>(() => vault.Create(UserId));
    }

    [Fact]
    public void TheVaultsPublicKeyOfAnotherPairRefusesTheToken()
    {
        var made = Elevation().Create(UserId);

        Assert.Null(new JwtElevationTokenService(PublicOnly(Other), _time).Validate(made.Token));
    }

    [Fact]
    public void ATokenSignedWithHmacUsingThePublicKeyAsTheSecretIsRefused()
    {
        // The classic mix-up: the public key is not secret, so whoever has it makes an HS256 token with it. Only ES256 is accepted.
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Convert.FromBase64String(Main.PublicKey));
        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("uid", UserId.ToString())]),
            NotBefore = _time.GetUtcNow().UtcDateTime,
            Expires = _time.GetUtcNow().UtcDateTime.AddMinutes(5),
            Issuer = "issuer",
            Audience = "audience:elevation",
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256)
        };
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var forged = handler.WriteToken(handler.CreateToken(descriptor));

        Assert.Null(new JwtElevationTokenService(PublicOnly(), _time).Validate(forged));
        Assert.Null(Elevation().Validate(forged));
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
