using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.IntegrationTests;

/// <summary>
/// Who may use what without signing in. Anything that does not say so needs a signed-in user (the fallback policy), so a new endpoint cannot
/// go live open by forgetting an attribute; and the endpoints that are open are exactly the ones listed here: opening one more has to change
/// this test, which is where somebody looks twice.
/// </summary>
public class AnonymousEndpointsTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task WhatDoesNotSayWhoMayUseItNeedsASignedInUser()
    {
        var policy = await api.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, requirement => requirement is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void TheEndpointsOpenToAnybodyAreExactlyThese()
    {
        var open = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => $"{string.Join("/", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])} /{endpoint.RoutePattern.RawText?.TrimStart('/')}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
        [
            // the lists the sign-in and the panel's forms are made of (no account data)
            "GET /api/Lookups/exchanges",
            "GET /api/Lookups/market-data-support",
            "GET /api/Lookups/markets",
            "GET /api/Lookups/symbols",
            "GET /api/Lookups/time-frames",
            // the API's own description, only where the host runs as Development (this one does)
            "GET /openapi/{documentName}.json",
            // signing in: every step before there is a session
            "POST /api/Auth/login",
            "POST /api/Auth/login/two-factor",
            "POST /api/Auth/login/two-factor/email",
            "POST /api/Auth/refresh-token",
            "POST /api/Auth/setup/password",
            "POST /api/Auth/setup/two-factor/begin",
            "POST /api/Auth/setup/two-factor/confirm"
        ], open);
    }

    [Fact]
    public async Task ASignedOutVisitorCannotTellWhichAddressesExist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var visitor = api.CreateClient();

        // Not signed in: the same answer for an address that is there and one that is not.
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/does-not-exist", ct)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await visitor.GetAsync("/api/admin/users", ct)).StatusCode);

        // Signed in, the address that is not there is a plain not found.
        var token = (await (await api.AdminLoginAsync(api.CreateClient(), ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(ct))!.AccessToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/does-not-exist");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await api.CreateClient().SendAsync(request, ct)).StatusCode);
    }
}
