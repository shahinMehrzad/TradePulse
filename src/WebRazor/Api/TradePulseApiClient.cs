using System.Net;
using System.Net.Http.Json;

namespace WebRazor.Api;

/// <summary>Typed client for the signed-in admin's calls. <see cref="ApiAuthHandler"/> supplies the token.</summary>
public sealed class TradePulseApiClient(HttpClient http)
{
    /// <returns>The admin the token belongs to, or null when the session is no longer accepted by the API.</returns>
    public async Task<AdminInfo?> GetMeAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("api/admin/me", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdminInfo>(cancellationToken);
    }
}
