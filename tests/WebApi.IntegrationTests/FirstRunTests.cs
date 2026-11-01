using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>What the very first sign-in on a fresh server looks like. Has its own fixture, so nothing has set the admin up yet.</summary>
public class FirstRunTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Step(string Stage, string? AccessToken, string? RefreshToken, bool MustChangePassword, bool NeedsTwoFactorSetup);

    [Fact]
    public async Task TheSeededAdministratorHasToChangeThePasswordAndEnrolTwoFactorBeforeGettingAnyToken()
    {
        var response = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, Ct);

        response.EnsureSuccessStatusCode();
        var step = (await response.Content.ReadFromJsonAsync<Step>(Ct))!;
        Assert.Equal("SetupRequired", step.Stage);
        Assert.True(step.MustChangePassword);
        Assert.True(step.NeedsTwoFactorSetup);
        Assert.Null(step.AccessToken);
        Assert.Null(step.RefreshToken);
    }
}
