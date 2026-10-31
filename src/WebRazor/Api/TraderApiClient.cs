using System.Net.Http.Json;

namespace WebRazor.Api;

/// <summary>Where the Trader program (the vault of the exchange keys) is reached on the stack's internal network; empty when there is none.</summary>
public class TraderOptions
{
    public const string SectionName = "Trader";

    public string BaseUrl { get; set; } = string.Empty;

    public bool Enabled => Uri.TryCreate(BaseUrl, UriKind.Absolute, out _);
}

/// <summary>
/// The signed-in administrator's calls to the Trader. It goes with the user's own access token and, once they confirmed with a code, the
/// confirmation token (<see cref="ApiAuthHandler"/> adds both), so the Trader decides for itself who is asking; the panel is not trusted
/// to say it.
/// </summary>
public sealed class TraderApiClient(HttpClient http)
{
    private const string NotFound = "That key does not exist (anymore). Reload the page.";

    /// <returns>The user's own keys, or null when the session is no longer accepted.</returns>
    public async Task<IReadOnlyList<ExchangeKeyInfo>?> GetKeysAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("keys", cancellationToken);
        if (TradePulseApiClient.IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ExchangeKeyInfo>>(cancellationToken);
    }

    public async Task<ApiResult> CreateAsync(string label, string environment, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("keys", new { Label = label, Environment = environment }, cancellationToken);
        return await TradePulseApiClient.ToResultAsync(response, NotFound, cancellationToken);
    }

    // ---- trading

    /// <returns>The limits and the halt switch of the user, or null when the session is no longer accepted.</returns>
    public async Task<TradingState?> GetTradingStateAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("trading/state", cancellationToken);
        if (TradePulseApiClient.IsRejected(response))
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TradingState>(cancellationToken);
    }

    public async Task<IReadOnlyList<TradingOrder>> GetOrdersAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("trading/orders", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<TradingOrder>>(cancellationToken) ?? [];
    }

    public async Task<ApiResult> PlaceOrderAsync(Guid keyId, string symbol, string side, decimal quantity, bool reduceOnly, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("trading/orders", new { KeyId = keyId, Symbol = symbol, Side = side, Quantity = quantity, ReduceOnly = reduceOnly }, cancellationToken);
        return await TradePulseApiClient.ToResultAsync(response, NotFound, cancellationToken);
    }

    public async Task<ApiResult> HaltAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync("trading/halt", content: null, cancellationToken);
        return await TradePulseApiClient.ToResultAsync(response, NotFound, cancellationToken);
    }

    public async Task<ApiResult> ResumeAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync("trading/resume", content: null, cancellationToken);
        return await TradePulseApiClient.ToResultAsync(response, NotFound, cancellationToken);
    }

    /// <summary>Gives the API key Binance made for the public key; the Trader then asks Binance whether it may hold the key.</summary>
    public async Task<ApiResult> SetApiKeyAsync(Guid id, string apiKey, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"keys/{id}/api-key", new { ApiKey = apiKey }, cancellationToken);
        return await TradePulseApiClient.ToResultAsync(response, NotFound, cancellationToken);
    }

    public async Task<ApiResult> VerifyAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync($"keys/{id}/verify", content: null, cancellationToken);
        return await TradePulseApiClient.ToResultAsync(response, NotFound, cancellationToken);
    }

    public async Task<ApiResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"keys/{id}", cancellationToken);
        return await TradePulseApiClient.ToResultAsync(response, NotFound, cancellationToken);
    }
}
