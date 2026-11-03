using System.Security.Cryptography;
using Application.Interfaces;
using Infrastructure.Configurations;
using Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Tests.Shared;

namespace Infrastructure.UnitTests;

/// <summary>The signing keys behind the tokens: what is accepted at start, and what is refused with a message that names the setting and leaks nothing.</summary>
public class JwtKeysTests
{
    private static readonly JwtTestKeys Pair = JwtTestKeys.New();
    private static readonly JwtTestKeys Another = JwtTestKeys.New();

    private static string Message(JwtConfigurations config) => Assert.Throws<InvalidOperationException>(() => JwtKeys.From(config)).Message;

    [Fact]
    public void AnApiHasTheKeyToSignAndToCheck()
    {
        var keys = JwtKeys.From(new JwtConfigurations { PrivateKey = Pair.PrivateKey });

        Assert.NotNull(keys.SigningKey);
        Assert.NotNull(keys.VerificationKey);
    }

    [Fact]
    public void AProgramWithOnlyThePublicKeyCannotSign()
    {
        var keys = JwtKeys.From(new JwtConfigurations { PublicKey = Pair.PublicKey });

        Assert.Null(keys.SigningKey);
        Assert.NotNull(keys.VerificationKey);
    }

    [Fact]
    public void ThePublicKeyOfTheSamePairIsAccepted()
    {
        Assert.NotNull(JwtKeys.From(new JwtConfigurations { PrivateKey = Pair.PrivateKey, PublicKey = Pair.PublicKey }).SigningKey);
    }

    [Fact]
    public void ThePublicKeyOfAnotherPairIsRefused()
    {
        var message = Message(new JwtConfigurations { PrivateKey = Pair.PrivateKey, PublicKey = Another.PublicKey });

        Assert.Contains("not the public half", message);
    }

    [Fact]
    public void NoKeyAtAllIsRefused()
    {
        var message = Message(new JwtConfigurations());

        Assert.Contains("JwtConfigurations:PrivateKey", message);
        Assert.Contains("make-jwt-keys.sh", message);
    }

    [Theory]
    [InlineData("not base64 at all !!!")]
    [InlineData("dGhpcyBpcyBub3QgYSBrZXk=")]   // valid base64, not a key
    public void GarbageIsRefusedByName(string value)
    {
        Assert.Contains("JwtConfigurations:PrivateKey", Message(new JwtConfigurations { PrivateKey = value }));
        Assert.Contains("JwtConfigurations:PublicKey", Message(new JwtConfigurations { PublicKey = value }));
    }

    [Fact]
    public void AMessageNeverContainsTheKey()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(40));

        Assert.DoesNotContain(secret, Message(new JwtConfigurations { PrivateKey = secret }));
        Assert.DoesNotContain(Pair.PrivateKey, Message(new JwtConfigurations { PrivateKey = Pair.PrivateKey, PublicKey = Another.PublicKey }));
    }

    [Fact]
    public void AKeyOnAnotherCurveIsRefused()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        var message = Message(new JwtConfigurations { PrivateKey = Convert.ToBase64String(p384.ExportPkcs8PrivateKey()) });

        Assert.Contains("P-256", message);
    }

    [Fact]
    public void AnRsaKeyIsRefused()
    {
        using var rsa = RSA.Create(2048);

        Assert.Contains("JwtConfigurations:PrivateKey", Message(new JwtConfigurations { PrivateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()) }));
    }

    [Fact]
    public void TheAccessTokenServiceNeedsThePrivateKey()
    {
        var publicOnly = Options.Create(new JwtConfigurations { PublicKey = Pair.PublicKey, Issuer = "i", Audience = "a" });

        Assert.Throws<InvalidOperationException>(() =>
            new JwtAccessTokenService(publicOnly).Create(new AuthenticatedUser(Guid.NewGuid(), "admin", "admin@example.com", ["Administrator"])));
    }
}
