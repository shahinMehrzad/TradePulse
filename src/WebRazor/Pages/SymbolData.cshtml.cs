using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>The funding rate and liquidations to collect, chosen per symbol (they have no time frame, so they are not part of a candle series).</summary>
public class SymbolDataModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    private const string FundingRate = "FundingRate";
    private const string Liquidation = "Liquidation";

    public IReadOnlyList<SymbolDataSetting> Settings { get; private set; } = [];

    public Lookups Lookups { get; private set; } = Lookups.Empty;

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Saved { get; private set; }

    [BindProperty]
    public SymbolDataForm Input { get; set; } = new();

    /// <summary>True while the form changes an existing setting instead of adding one.</summary>
    public bool IsEditing => Input.Id != 0;

    public class SymbolDataForm
    {
        public int Id { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose an exchange.")]
        public int ExchangeId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose a market.")]
        public int MarketId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose a symbol.")]
        public int SymbolId { get; set; }

        public bool CollectFundingRate { get; set; }

        /// <summary>Where the funding rate history starts. Needed (and only sent) when the funding rate is collected.</summary>
        [DataType(DataType.Date)]
        public DateTime? FundingStartDate { get; set; }

        public bool CollectLiquidation { get; set; }
    }

    public SelectList ExchangeItems => new(Lookups.Exchanges, nameof(Lookup.Id), nameof(Lookup.Name), Input.ExchangeId);
    public SelectList MarketItems => new(Lookups.Markets, nameof(Lookup.Id), nameof(Lookup.Name), Input.MarketId);
    public SelectList SymbolItems => new(
        Lookups.Symbols.Select(s => new { s.Id, Label = string.IsNullOrWhiteSpace(s.Description) ? s.Name : $"{s.Name} ({s.Description})" }),
        "Id", "Label", Input.SymbolId);

    public string ExchangeName(int id) => Lookups.Exchanges.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";
    public string MarketName(int id) => Lookups.Markets.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";
    public string SymbolName(int id) => Lookups.Symbols.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";

    /// <summary>Whether the market the form describes right now has a funding rate (the checkbox is disabled when not).</summary>
    public bool FundingRateAvailable => Lookups.Supports(Input.ExchangeId, Input.MarketId, FundingRate);

    public bool LiquidationAvailable => Lookups.Supports(Input.ExchangeId, Input.MarketId, Liquidation);

    /// <summary>Says where the funding rate exists, from the rules the API publishes.</summary>
    public string FundingRateRule => Rule(FundingRate);

    public string LiquidationRule => Rule(Liquidation);

    private string Rule(string dataType)
    {
        var markets = Lookups.MarketDataSupport.Where(s => s.DataType == dataType).Select(s => MarketName(s.MarketId)).Distinct().ToList();
        return markets.Count == 0 ? "Not available on any market." : $"{string.Join(" and ", markets)} only.";
    }

    /// <summary>The Edit button links here with <c>?edit=id</c>, which fills the form with that setting.</summary>
    public async Task<IActionResult> OnGetAsync(int? edit, CancellationToken cancellationToken)
    {
        var result = await LoadAsync(cancellationToken);
        if (result is not null)
            return result;

        if (edit is { } id)
        {
            var setting = Settings.FirstOrDefault(s => s.Id == id);
            if (setting is null)
                Errors = ["That setting does not exist (anymore)."];
            else
                Input = new SymbolDataForm
                {
                    Id = setting.Id,
                    ExchangeId = setting.ExchangeId,
                    MarketId = setting.MarketId,
                    SymbolId = setting.SymbolId,
                    CollectFundingRate = setting.CollectFundingRate,
                    FundingStartDate = setting.FundingStartDate,
                    CollectLiquidation = setting.CollectLiquidation
                };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (Input.CollectFundingRate && Input.FundingStartDate is null)
            ModelState.AddModelError("Input.FundingStartDate", "Enter the date to collect the funding rate from.");

        if (ModelState.IsValid)
        {
            // The date input has no time or zone; the API reads a "Z" date as UTC, which is what is meant.
            var request = new SymbolDataRequest(
                Input.ExchangeId, Input.MarketId, Input.SymbolId,
                Input.CollectFundingRate,
                Input.CollectFundingRate ? DateTime.SpecifyKind(Input.FundingStartDate!.Value.Date, DateTimeKind.Utc) : null,
                Input.CollectLiquidation);

            var result = await CallAsync(() => IsEditing
                ? api.UpdateSymbolDataAsync(Input.Id, request, cancellationToken)
                : api.AddSymbolDataAsync(request, cancellationToken));

            if (result.SessionRejected)
                return await EndSessionAsync();

            if (result.Success)
            {
                Saved = IsEditing ? "The setting was changed." : "The setting was added.";
                ResetForm();
            }
            else
            {
                Errors = result.Errors;
            }
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        // The form is not part of this request. Binding still fills Input.Id from the "id" of the removed row (an empty prefix is
        // tried when nothing is posted under "Input"), which would turn the next "add" into a change of a row that is gone.
        ResetForm();
        var result = await CallAsync(() => api.DeleteSymbolDataAsync(id, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = "The setting was removed.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>
    /// Back to an empty "add" form. The posted values have to go too: a tag helper shows what was posted (the hidden id, the old
    /// choices) in preference to the model, so resetting <see cref="Input"/> alone would leave the form still pointing at the old row.
    /// </summary>
    private void ResetForm()
    {
        ModelState.Clear();
        Input = new SymbolDataForm();
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
            var settings = await api.GetSymbolDataAsync(cancellationToken);
            if (settings is null)
                return await EndSessionAsync();

            Settings = settings;
            Lookups = await api.GetLookupsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            Errors = [.. Errors, "The API is not reachable right now."];
        }

        return null;
    }
}
