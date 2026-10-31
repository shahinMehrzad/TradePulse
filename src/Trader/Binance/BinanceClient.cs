using System.Text.Json;
using Microsoft.Extensions.Options;
using Trader.Vault;

namespace Trader.Binance;

/// <summary>Where a key lives: the real exchange, or Binance's Demo environment (play money, its own host, its own keys).</summary>
public enum KeyEnvironment { Live, Demo }

public sealed class BinanceOptions
{
    public const string SectionName = "Binance";

    public string FuturesBaseUrl { get; set; } = "https://fapi.binance.com";

    /// <summary>The Futures REST host of the Demo environment.</summary>
    public string DemoFuturesBaseUrl { get; set; } = "https://demo-fapi.binance.com";

    /// <summary>The Wallet API lives on the spot host; it answers what a key may do (<c>/sapi/v1/account/apiRestrictions</c>).</summary>
    public string SpotBaseUrl { get; set; } = "https://api.binance.com";

    /// <summary>
    /// True: a live key is activated only after Binance itself said that withdrawals and transfers are off and an IP restriction is set.
    /// If Binance cannot be asked, the key is not activated. Demo keys never get this check (the Demo environment has no such endpoint
    /// and holds no real money), whatever this says.
    /// </summary>
    public bool RequirePermissionCheck { get; set; } = true;

    public string FuturesUrl(KeyEnvironment environment) => environment == KeyEnvironment.Demo ? DemoFuturesBaseUrl : FuturesBaseUrl;
}

/// <param name="Status">The HTTP status, or 0 when Binance could not be reached.</param>
public sealed record BinanceReply(int Status, int? Code, string? Message, JsonElement? Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// Requests to Binance. The API key is sent in the header and the signature in the query, as documented; neither is
/// ever logged (the address holds the signature, so this client logs nothing about its requests).
/// </summary>
public sealed class BinanceClient(IHttpClientFactory httpClients, TimeProvider time)
{
    public const string HttpClientName = "binance";

    public Task<BinanceReply> GetSignedAsync(string baseUrl, string path, string apiKey, IRequestSigner signer, CancellationToken cancellationToken) =>
        SendSignedAsync(HttpMethod.Get, baseUrl, path, parameters: "", apiKey, signer, cancellationToken);

    /// <param name="parameters">The query parameters of the call, already encoded (for example <c>symbol=BTCUSDT&amp;side=BUY</c>).</param>
    public Task<BinanceReply> PostSignedAsync(string baseUrl, string path, string parameters, string apiKey, IRequestSigner signer, CancellationToken cancellationToken) =>
        SendSignedAsync(HttpMethod.Post, baseUrl, path, parameters, apiKey, signer, cancellationToken);

    /// <summary>A call that needs no key (market data).</summary>
    public async Task<BinanceReply> GetPublicAsync(string baseUrl, string pathAndQuery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}{pathAndQuery}");
        return await SendAsync(request, cancellationToken);
    }

    private async Task<BinanceReply> SendSignedAsync(
        HttpMethod method, string baseUrl, string path, string parameters, string apiKey, IRequestSigner signer, CancellationToken cancellationToken)
    {
        var payload = $"{(parameters.Length > 0 ? parameters + "&" : "")}timestamp={time.GetUtcNow().ToUnixTimeMilliseconds()}&recvWindow=5000";
        var signature = Uri.EscapeDataString(signer.Sign(payload));
        using var request = new HttpRequestMessage(method, $"{baseUrl.TrimEnd('/')}{path}?{payload}&signature={signature}");
        request.Headers.Add("X-MBX-APIKEY", apiKey);
        return await SendAsync(request, cancellationToken);
    }

    private async Task<BinanceReply> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(text);
                var body = document.RootElement.Clone();
                int? code = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var c) && c.TryGetInt32(out var value) ? value : null;
                var message = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("msg", out var m) ? m.GetString() : null;
                return new BinanceReply((int)response.StatusCode, code, message, body);
            }
            catch (JsonException)
            {
                return new BinanceReply((int)response.StatusCode, null, null, null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new BinanceReply(0, null, null, null);
        }
    }
}
