using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Application.Interfaces;
using Infrastructure.Configurations;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Tests.Shared;

namespace Trader.IntegrationTests;

/// <summary>
/// What the vault believes. It has the PUBLIC key of the API only, so it can check a token and never make one: a token signed by anything
/// else, or by a trick with the public key, is refused, and the vault will not even start if it is given the signing key.
/// </summary>
public class TokenTrustTests(TraderFixture trader) : IClassFixture<TraderFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JwtConfigurations SignedBy(JwtTestKeys keys) =>
        new() { PrivateKey = keys.PrivateKey, Issuer = TraderFixture.Jwt.Issuer, Audience = TraderFixture.Jwt.Audience, DurationInMinutes = 30 };

    private static string AccessToken(JwtConfigurations signer, Guid id) =>
        new JwtAccessTokenService(Options.Create(signer)).Create(new AuthenticatedUser(id, "forger", "forger@example.com", [TraderFixture.AdminRole])).Token;

    private static string HmacTokenWithThePublicKeyAsSecret(Guid id)
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(TraderFixture.SigningKeys.PublicKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            TraderFixture.Jwt.Issuer, TraderFixture.Jwt.Audience,
            [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, "forger"), new Claim(ClaimTypes.Role, TraderFixture.AdminRole)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private Task<HttpResponseMessage> ListKeysAsync(string accessToken, string? elevation = null)
    {
        var caller = new TraderFixture.Caller(Guid.NewGuid(), "forger", accessToken, elevation ?? "", "");
        return trader.SendAsync(HttpMethod.Get, "/keys", caller, elevated: false);
    }

    [Fact]
    public async Task ATokenOfTheApiIsBelieved()
    {
        var admin = trader.Administrator("believed");

        Assert.Equal(HttpStatusCode.OK, (await trader.SendAsync(HttpMethod.Get, "/keys", admin, elevated: false)).StatusCode);
    }

    [Fact]
    public async Task ATokenSignedByAnotherKeyIsRefused()
    {
        var forged = AccessToken(SignedBy(JwtTestKeys.New()), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, (await ListKeysAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task AnHs256TokenMadeWithThePublicKeyAsSecretIsRefused()
    {
        // The public key is not a secret, so this is what somebody who only has the vault's settings can try.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ListKeysAsync(HmacTokenWithThePublicKeyAsSecret(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task AConfirmationOfTheApiIsNotAConfirmationOfTheVaultEvenWithTheRightKey()
    {
        var admin = trader.Administrator("api-confirmed");
        // What the API gives after a sign-in code, made with the REAL key: a hacked API can make exactly this for anybody. The vault does not take it.
        var apiConfirmation = new JwtElevationTokenService(Options.Create(TraderFixture.Jwt), TimeProvider.System).Create(admin.Id).Token;

        var asTheVaultsHeader = await trader.SendAsync(HttpMethod.Post, "/keys", admin, new { Label = "x", Environment = "Demo" }, elevated: true, elevation: apiConfirmation);
        Assert.Equal(HttpStatusCode.Forbidden, asTheVaultsHeader.StatusCode);
        Assert.Contains("step_up_required", await asTheVaultsHeader.Content.ReadAsStringAsync(Ct));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/keys") { Content = System.Net.Http.Json.JsonContent.Create(new { Label = "x", Environment = "Demo" }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin.AccessToken);
        request.Headers.Add("X-Elevation", apiConfirmation);   // the header the API's own endpoints read
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.CreateClient().SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public void TheVaultRefusesToStartHoldingTheSigningKey()
    {
        using var factory = trader.WithWebHostBuilder(builder => builder.UseSetting("JwtConfigurations:PrivateKey", TraderFixture.SigningKeys.PrivateKey));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("must not hold the signing key", failure.ToString());
        Assert.DoesNotContain(TraderFixture.SigningKeys.PrivateKey, failure.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("dGhpcyBpcyBub3QgYSBrZXk=")]
    public void TheVaultRefusesToStartWithoutAValidPublicKey(string key)
    {
        using var factory = trader.WithWebHostBuilder(builder => builder.UseSetting("JwtConfigurations:PublicKey", key));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("JwtConfigurations:PublicKey", failure.ToString());
    }
}
