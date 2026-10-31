using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// The symbols the collector can be told about, and where each is listed. A series or a funding rate setting can only be chosen
/// for a symbol that is listed on its exchange market, and the API asks the exchange before it lists one.
/// </summary>
public class SymbolsModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public IReadOnlyList<SymbolListingItem> Listings { get; private set; } = [];

    public Lookups Lookups { get; private set; } = Lookups.Empty;

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Saved { get; private set; }

    [BindProperty]
    public SymbolForm NewSymbol { get; set; } = new();

    [BindProperty]
    public ListingForm NewListing { get; set; } = new();

    // No validation attributes here: both forms are bound on every post, and the API refuses what is missing with a reason of its own.
    public class SymbolForm
    {
        public string? Name { get; set; }

        public string? Description { get; set; }
    }

    public class ListingForm
    {
        public int ExchangeId { get; set; }

        public int MarketId { get; set; }

        public int SymbolId { get; set; }

        /// <summary>The name the exchange itself uses, e.g. BTCUSDT.</summary>
        public string? ExchangeSymbol { get; set; }
    }

    public SelectList ExchangeItems => new(Lookups.Exchanges, nameof(Lookup.Id), nameof(Lookup.Name), NewListing.ExchangeId);
    public SelectList MarketItems => new(Lookups.Markets, nameof(Lookup.Id), nameof(Lookup.Name), NewListing.MarketId);
    public SelectList SymbolItems => new(Lookups.Symbols, nameof(SymbolLookup.Id), nameof(SymbolLookup.Name), NewListing.SymbolId);

    /// <summary>On how many exchange markets the symbol is listed; a symbol that is listed somewhere cannot be removed.</summary>
    public int ListedOn(int symbolId) => Listings.Count(l => l.SymbolId == symbolId);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken) ?? Page();

    public async Task<IActionResult> OnPostAddSymbolAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync(() => api.AddSymbolAsync(NewSymbol.Name, NewSymbol.Description, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
        {
            Saved = "The symbol was added. List it on an exchange market next.";
            ResetForms();
        }
        else
        {
            Errors = result.Errors;
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostAddListingAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync(() => api.AddListingAsync(
            NewListing.ExchangeId, NewListing.MarketId, NewListing.SymbolId, NewListing.ExchangeSymbol, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
        {
            Saved = "The symbol was listed. It can be chosen for a series now.";
            ResetForms();
        }
        else
        {
            Errors = result.Errors;
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostDeleteSymbolAsync(int id, CancellationToken cancellationToken)
    {
        // The forms are not part of this request, and the posted "id" must not leak into them.
        ResetForms();
        var result = await CallAsync(() => api.DeleteSymbolAsync(id, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = "The symbol was removed.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostDeleteListingAsync(int id, CancellationToken cancellationToken)
    {
        ResetForms();
        var result = await CallAsync(() => api.DeleteListingAsync(id, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = "The listing was removed. Tables already collected stay in the database.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>
    /// Back to empty forms. The posted values have to go too: a tag helper shows what was posted in preference to the model, so
    /// resetting the properties alone would leave the old choices in the fields.
    /// </summary>
    private void ResetForms()
    {
        ModelState.Clear();
        NewSymbol = new SymbolForm();
        NewListing = new ListingForm();
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

    /// <returns>A redirect when the session is over, otherwise null (the page then renders with whatever could be loaded).</returns>
    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var listings = await api.GetListingsAsync(cancellationToken);
            if (listings is null)
                return await EndSessionAsync();

            Listings = listings;
            Lookups = await api.GetLookupsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            Errors = [.. Errors, "The API is not reachable right now."];
        }

        return null;
    }
}
