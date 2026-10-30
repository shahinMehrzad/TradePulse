using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;

namespace WebRazor.Api;

/// <summary>
/// Adds the signed-in session's bearer token to every WebApi call and rotates it shortly before it expires.
/// Refresh tokens are single-use, so refreshes are serialised: two parallel requests must not both spend the same one.
/// </summary>
public sealed class ApiAuthHandler(
    IHttpContextAccessor httpContextAccessor,
    SessionTokenStore store,
    IHttpClientFactory clientFactory,
    TimeProvider time,
    ILogger<ApiAuthHandler> logger) : DelegatingHandler
{
    public const string SessionClaim = "sid";
    public const string DeviceHeader = "X-Device-Id";
    public const string RawClientName = "api-raw";

    private static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sessionId = httpContextAccessor.HttpContext?.User.FindFirstValue(SessionClaim);
        if (sessionId is null || !store.TryGet(sessionId, out var tokens))
            return await base.SendAsync(request, cancellationToken);

        if (tokens.ExpiresAt - time.GetUtcNow().UtcDateTime <= RefreshWindow)
            tokens = await RefreshAsync(sessionId, cancellationToken) ?? tokens;

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        // Says which device is asking, so "sign out my other devices" leaves this one alone.
        request.Headers.TryAddWithoutValidation(DeviceHeader, sessionId);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<TokenSet?> RefreshAsync(string sessionId, CancellationToken cancellationToken)
    {
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            // Someone may have refreshed while we waited for the lock.
            if (!store.TryGet(sessionId, out var current))
                return null;
            if (current.ExpiresAt - time.GetUtcNow().UtcDateTime > RefreshWindow)
                return current;

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/refresh-token")
            {
                Content = JsonContent.Create(new { current.RefreshToken })
            };
            request.Headers.Add(DeviceHeader, sessionId);

            using var response = await clientFactory.CreateClient(RawClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Expired or already used: the session is over, the next page load goes to the login page.
                logger.LogWarning("Token refresh was rejected with {StatusCode}; ending the session.", (int)response.StatusCode);
                store.Remove(sessionId);
                return null;
            }

            var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken)
                        ?? throw new InvalidOperationException("Empty refresh response.");
            if (login.AccessToken is null || login.RefreshToken is null || login.ExpiresAt is null)
            {
                store.Remove(sessionId);
                return null;
            }

            var refreshed = new TokenSet(login.AccessToken, login.RefreshToken, login.ExpiresAt.Value);
            store.Update(sessionId, refreshed);
            return refreshed;
        }
        finally
        {
            RefreshLock.Release();
        }
    }
}
