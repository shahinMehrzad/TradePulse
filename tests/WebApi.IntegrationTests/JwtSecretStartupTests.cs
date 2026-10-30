using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebApi.IntegrationTests;

public class JwtSecretStartupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void TheHostRefusesToStartWithoutAStrongSigningSecret(string secret)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("JwtConfigurations:Secret", secret));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("JwtConfigurations:Secret", failure.ToString());
    }

    [Fact]
    public void TheCommittedSettingsAloneAreNotEnoughToStart()
    {
        // No committed file carries a signing key; every environment has to supply its own (Production reads no user secrets either).
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production"));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("JwtConfigurations:Secret", failure.ToString());
    }
}
