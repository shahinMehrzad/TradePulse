using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// The symbols the live watch follows: the one that tells the admin about a sudden price move. Apart from what the collector collects,
/// on purpose, so the chat is not filled with symbols the admin does not trade.
/// </summary>
public class ShockWatchModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public IReadOnlyList<ShockWatchItem> Items { get; private set; } = [];

    public Lookups Lookups { get; private set; } = Lookups.Empty;

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Saved { get; private set; }

    [BindProperty]
    public WatchForm Input { get; set; } = new();

    public class WatchForm
    {
        [Range(1, int.MaxValue, ErrorMessage = "Choose an exchange.")]
        public int ExchangeId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose a market.")]
        public int MarketId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose a symbol.")]
        public int SymbolId { get; set; }
    }

    public SelectList ExchangeItems => new(Lookups.Exchanges, nameof(Lookup.Id), nameof(Lookup.Name), Input.ExchangeId);
    public SelectList MarketItems => new(Lookups.Markets, nameof(Lookup.Id), nameof(Lookup.Name), Input.MarketId);
    public SelectList SymbolItems => new(
        Lookups.Symbols.Select(s => new { s.Id, Label = string.IsNullOrWhiteSpace(s.Description) ? s.Name : $"{s.Name} ({s.Description})" }),
        "Id", "Label", Input.SymbolId);

    public string ExchangeName(int id) => Lookups.Exchanges.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";
    public string MarketName(int id) => Lookups.Markets.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";
    public string SymbolName(int id) => Lookups.Symbols.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";

    /// <summary>How many symbols are watched right now (on the list and switched on).</summary>
    public int WatchedCount => Items.Count(i => i.IsEnabled);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken) ?? Page();

    public async Task<IActionResult> OnPostAddAsync(CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            var result = await CallAsync(() => api.AddShockWatchAsync(Input.ExchangeId, Input.MarketId, Input.SymbolId, cancellationToken));
            if (result.SessionRejected)
                return await EndSessionAsync();

            if (result.Success)
            {
                Saved = "The symbol is on the watch list.";
                ResetForm();
            }
            else
            {
                Errors = result.Errors;
            }
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostSwitchAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        ResetForm();
        var result = await CallAsync(() => api.SetShockWatchEnabledAsync(id, enabled, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = enabled ? "The watch of that symbol is on." : "The watch of that symbol is off.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        ResetForm();
        var result = await CallAsync(() => api.DeleteShockWatchAsync(id, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = "The symbol was taken off the watch list.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>Back to an empty form. The posted values have to go too: a tag helper shows what was posted in preference to the model.</summary>
    private void ResetForm()
    {
        ModelState.Clear();
        Input = new WatchForm();
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
            var items = await api.GetShockWatchAsync(cancellationToken);
            if (items is null)
                return await EndSessionAsync();

            Items = items;
            Lookups = await api.GetLookupsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            Errors = [.. Errors, "The API is not reachable right now."];
        }

        return null;
    }
}
