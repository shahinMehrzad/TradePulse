using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebApi.IntegrationTests;

public class JwtKeyStartupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("dGhpcyBpcyBub3QgYSBrZXk=")]   // valid base64, not a key
    public void TheHostRefusesToStartWithoutAValidSigningKey(string key)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("JwtConfigurations:PrivateKey", key));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("JwtConfigurations:PrivateKey", failure.ToString());
        Assert.DoesNotContain(key.Length > 8 ? key : "no-key-given-to-look-for", failure.ToString());
    }

    [Fact]
    public void TheHostRefusesAKeyOnAnotherCurve()
    {
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("JwtConfigurations:PrivateKey", Convert.ToBase64String(p384.ExportPkcs8PrivateKey())));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("P-256", failure.ToString());
    }

    [Fact]
    public void ThePublicKeyAloneCannotRunTheApi()
    {
        // The API signs the tokens; with only the public key there would be nothing to sign with.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("JwtConfigurations:PublicKey", Tests.Shared.JwtTestKeys.New().PublicKey));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("JwtConfigurations:PrivateKey must be set", failure.ToString());
    }

    [Fact]
    public void TheCommittedSettingsAloneAreNotEnoughToStart()
    {
        // No committed file carries a signing key; every environment has to supply its own (Production reads no user secrets either).
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production"));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("JwtConfigurations:PrivateKey", failure.ToString());
    }
}
