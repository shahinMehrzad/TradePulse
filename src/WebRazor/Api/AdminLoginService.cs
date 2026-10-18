using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebRazor.Api;

public enum LoginFailure { None, InvalidCredentials, NotAnAdmin, TooManyAttempts, ApiUnavailable }

public record LoginOutcome(LoginFailure Failure, string? SessionId = null, AdminInfo? Admin = null);

/// <summary>
/// Signs in against WebApi and lets only an administrator into the panel. The role check is WebApi's own
/// (<c>GET /api/admin/me</c> answers 403 to anyone else), so the panel never decodes or trusts a token itself.
/// </summary>
public sealed class AdminLoginService(IHttpClientFactory clientFactory, SessionTokenStore store)
{
    public async Task<LoginOutcome> LoginAsync(string identifier, string password, CancellationToken cancellationToken)
    {
        var isEmail = identifier.Contains('@');
        // Doubles as the device id: refresh tokens are bound to it.
        var sessionId = Guid.NewGuid().ToString("N");
        var client = clientFactory.CreateClient(ApiAuthHandler.RawClientName);

        try
        {
            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
            {
                Content = JsonContent.Create(new
                {
                    Email = isEmail ? identifier : string.Empty,
                    Username = isEmail ? string.Empty : identifier,
                    Password = password
                })
            };
            loginRequest.Headers.Add(ApiAuthHandler.DeviceHeader, sessionId);

            using var loginResponse = await client.SendAsync(loginRequest, cancellationToken);
            if (loginResponse.StatusCode == HttpStatusCode.TooManyRequests)
                return new LoginOutcome(LoginFailure.TooManyAttempts);
            if (loginResponse.StatusCode == HttpStatusCode.Unauthorized)
                return new LoginOutcome(LoginFailure.InvalidCredentials);
            loginResponse.EnsureSuccessStatusCode();

            var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken)
                        ?? throw new InvalidOperationException("Empty login response.");

            using var meRequest = new HttpRequestMessage(HttpMethod.Get, "api/admin/me");
            meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
            using var meResponse = await client.SendAsync(meRequest, cancellationToken);
            if (meResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return new LoginOutcome(LoginFailure.NotAnAdmin);
            meResponse.EnsureSuccessStatusCode();

            var admin = await meResponse.Content.ReadFromJsonAsync<AdminInfo>(cancellationToken)
                        ?? throw new InvalidOperationException("Empty admin response.");

            store.Create(sessionId, new TokenSet(login.AccessToken, login.RefreshToken, login.ExpiresAt));
            return new LoginOutcome(LoginFailure.None, sessionId, admin);
        }
        catch (HttpRequestException)
        {
            return new LoginOutcome(LoginFailure.ApiUnavailable);
        }
    }
}
