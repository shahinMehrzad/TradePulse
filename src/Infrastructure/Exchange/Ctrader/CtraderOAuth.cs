using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.Exchange.Ctrader;

/// <summary>What the account owner lets the application do.</summary>
public enum CtraderScope
{
    /// <summary>View only. This is the one for data: a token that can trade is never held by the collector or the API.</summary>
    Accounts,

    /// <summary>View and trade.</summary>
    Trading,
}

/// <param name="AccessToken">What the connection signs the account in with. Expires after <paramref name="ExpiresIn"/> (about 30 days).</param>
/// <param name="RefreshToken">Makes a new pair of tokens without the owner; it does not expire, but each use invalidates it and the old access token.</param>
public sealed record CtraderTokens(string AccessToken, string RefreshToken, TimeSpan ExpiresIn)
{
    // The tokens are secrets: a log line or an exception text that prints this record must not print them.
    public override string ToString() => $"CtraderTokens {{ expires in {ExpiresIn.TotalDays:F0} days }}";
}

/// <summary>
/// cTrader's OAuth 2 flow for an Open API application: the owner of the account opens an address at cTrader, allows the application, and is sent
/// back to the application's registered redirect address with a short-lived <c>code</c> (one minute), which is exchanged for the tokens.
/// </summary>
public static class CtraderOAuth
{
    public const string AuthorizeEndpoint = "https://id.ctrader.com/my/settings/openapi/grantingaccess/";
    public const string TokenEndpoint = "https://openapi.ctrader.com/apps/token";

    /// <summary>The address the owner of the account opens to allow the application. <paramref name="redirectUri"/> must be one registered for it.</summary>
    public static Uri BuildAuthorizationUrl(string clientId, string redirectUri, CtraderScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var query = $"client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&scope={(scope == CtraderScope.Trading ? "trading" : "accounts")}&product=web";
        return new Uri($"{AuthorizeEndpoint}?{query}");
    }

    /// <summary>The code in the address cTrader sent the owner back to, or why there is none (the owner said no, or it is not that kind of request).</summary>
    public static bool TryReadCallback(Uri callback, out string code, out string? problem)
    {
        var query = System.Web.HttpUtility.ParseQueryString(callback.Query);
        code = query["code"] ?? string.Empty;

        if (code.Length > 0)
        {
            problem = null;
            return true;
        }

        problem = query["error"] is { Length: > 0 } error
            ? $"cTrader did not allow it ({error}{(query["error_description"] is { Length: > 0 } text ? $": {text}" : string.Empty)})."
            : "The address has no code.";
        return false;
    }

    public static Task<CtraderTokens> ExchangeCodeAsync(HttpClient http, string clientId, string clientSecret, string code, string redirectUri, CancellationToken cancellationToken) =>
        RequestAsync(http, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["client_id"] = clientId, ["client_secret"] = clientSecret,
        }, cancellationToken);

    /// <summary>Makes a new pair of tokens. The refresh token that was used and the access token that went with it stop working at once.</summary>
    public static Task<CtraderTokens> RefreshAsync(HttpClient http, string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken) =>
        RequestAsync(http, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = clientId, ["client_secret"] = clientSecret,
        }, cancellationToken);

    // cTrader's page lists GET for this endpoint and shows POST in its refresh example: GET first, POST when GET is not allowed. The secret travels in
    // the query either way, so no address and no HttpClient exception text (which can hold it) is ever passed on.
    private static async Task<CtraderTokens> RequestAsync(HttpClient http, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var query = string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var address = new Uri($"{TokenEndpoint}?{query}");

        try
        {
            var response = await SendAsync(http, address, HttpMethod.Get, cancellationToken);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                response.Dispose();
                response = await SendAsync(http, address, HttpMethod.Post, cancellationToken);
            }

            using var answer = response;
            var body = await answer.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new CtraderException("cTrader's token endpoint sent an empty answer.");

            if (!string.IsNullOrEmpty(body.ErrorCode))
                throw new CtraderException($"cTrader refused the token request ({body.ErrorCode}{(string.IsNullOrEmpty(body.Description) ? string.Empty : $": {body.Description}")}).", body.ErrorCode);
            if (string.IsNullOrEmpty(body.AccessToken) || string.IsNullOrEmpty(body.RefreshToken))
                throw new CtraderException($"cTrader's token endpoint answered {(int)answer.StatusCode} without tokens.");

            return new CtraderTokens(body.AccessToken, body.RefreshToken, TimeSpan.FromSeconds(body.ExpiresIn));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new CtraderException($"The token request to cTrader failed ({ex.GetType().Name}).");
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, Uri address, HttpMethod method, CancellationToken cancellationToken) =>
        http.SendAsync(new HttpRequestMessage(method, address), cancellationToken);

    private sealed class TokenResponse
    {
        [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
        [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expiresIn")] public long ExpiresIn { get; set; }
        [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
