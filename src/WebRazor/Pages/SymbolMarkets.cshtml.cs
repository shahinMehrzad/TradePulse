using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// One symbol and where it is listed: the exchange markets, the name each exchange uses for it, and what uses each listing. The admin
/// lists it on another exchange market here (the API asks the exchange whether it trades the name) or removes a listing nothing uses.
/// </summary>
public class SymbolMarketsModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    /// <summary>The symbol, from the address (<c>/Symbols/5</c>).</summary>
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    /// <summary>Where the list the admin came from stood, so that "back" returns to the same page and search.</summary>
    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public string? Keyword { get; set; }

    public SymbolDetail Detail { get; private set; } = null!;

    public Lookups Lookups { get; private set; } = Lookups.Empty;

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Saved { get; private set; }

    [BindProperty]
    public MarketForm Input { get; set; } = new();

    // No validation attributes: the API refuses what is missing or wrong with a reason of its own.
    public class MarketForm
    {
        /// <summary>The exchange market chosen, as "exchangeId:marketId".</summary>
        public string? Market { get; set; }

        public string? ExchangeSymbol { get; set; }
    }

    /// <summary>The exchange markets this symbol is not listed on yet, as the form offers them.</summary>
    public IReadOnlyList<(string Value, string Label)> AvailableMarkets =>
    [
        .. from exchange in Lookups.Exchanges
           from market in Lookups.Markets.OrderBy(m => m.Id)
           where !Detail.Markets.Any(l => l.ExchangeId == exchange.Id && l.MarketId == market.Id)
           select ($"{exchange.Id}:{market.Id}", $"{exchange.Name} {market.Name}")
    ];

    /// <summary>What most exchanges call it: BTC/USDT is BTCUSDT. Only a suggestion; the exchange has the last word.</summary>
    public string SuggestedExchangeSymbol => Detail.Name.Replace("/", "");

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken) ?? Page();

    public async Task<IActionResult> OnPostAddAsync(CancellationToken cancellationToken)
    {
        var chosen = (Input.Market ?? "").Split(':');
        if (chosen.Length != 2 || !int.TryParse(chosen[0], out var exchangeId) || !int.TryParse(chosen[1], out var marketId))
        {
            Errors = ["Choose the exchange market."];
        }
        else
        {
            var result = await CallAsync(() => api.AddListingAsync(exchangeId, marketId, Id, Input.ExchangeSymbol, cancellationToken));
            if (result.SessionRejected)
                return await EndSessionAsync();

            if (result.Success)
            {
                Saved = "The symbol was listed. It can be chosen for a series now.";
                ResetForm();
            }
            else
            {
                Errors = result.Errors;
            }
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int listingId, CancellationToken cancellationToken)
    {
        ResetForm();

        var result = await CallAsync(() => api.DeleteListingAsync(listingId, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = "The listing was removed. The tables already collected stay in the database.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>
    /// Back to an empty form. The posted values have to go too: a tag helper shows what was posted in preference to the model, so
    /// resetting <see cref="Input"/> alone would leave the old choices in the fields.
    /// </summary>
    private void ResetForm()
    {
        ModelState.Clear();
        Input = new MarketForm();
    }

    /// <summary>A write that could not reach the API is shown as an error, not as a crash.</summary>
    private static async Task<ApiResult> CallAsync(Func<Task<ApiResult>> write)
    {
        try
        {
            return await write();
        }
        catch (HttpRequestException)
        {
            return new ApiResult(false, ["The API is not reachable right now."]);
        }
    }

    /// <returns>A redirect (session over) or a 404 (no such symbol), otherwise null: the page then renders.</returns>
    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        PageNumber = Math.Max(1, PageNumber);

        try
        {
            var (detail, rejected) = await api.GetSymbolDetailAsync(Id, cancellationToken);
            if (rejected)
                return await EndSessionAsync();
            if (detail is null)
                return NotFound();

            Detail = detail;
            Lookups = await api.GetLookupsAsync(cancellationToken);
            return null;
        }
        catch (HttpRequestException)
        {
            // Without the symbol there is nothing to show; the list page says the API is down.
            return RedirectToPage("/Symbols", new { pageNumber = PageNumber, keyword = Keyword });
        }
    }
}
