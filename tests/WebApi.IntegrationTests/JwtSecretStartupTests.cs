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
    public void OutsideDevelopmentTheCommittedSettingsAloneAreNotEnoughToStart()
    {
        // The Development file carries the only built-in secret; production has to supply its own.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production"));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("JwtConfigurations:Secret", failure.ToString());
    }
}
