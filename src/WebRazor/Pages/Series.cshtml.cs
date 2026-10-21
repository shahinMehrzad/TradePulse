using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using WebRazor.Api;

namespace WebRazor.Pages;

public class SeriesModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public IReadOnlyList<CandleWorkerSeries> Series { get; private set; } = [];

    public Lookups Lookups { get; private set; } = Lookups.Empty;

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Saved { get; private set; }

    [BindProperty]
    public SeriesForm Input { get; set; } = new();

    /// <summary>True while the form changes an existing series instead of adding one.</summary>
    public bool IsEditing => Input.Id != 0;

    public class SeriesForm
    {
        public int Id { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose an exchange.")]
        public int ExchangeId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose a market.")]
        public int MarketId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose a symbol.")]
        public int SymbolId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Choose a time frame.")]
        public int TimeFrameId { get; set; }

        [Required(ErrorMessage = "Enter the date to collect from.")]
        [DataType(DataType.Date)]
        public DateTime? StartDate { get; set; }
    }

    public SelectList ExchangeItems => new(Lookups.Exchanges, nameof(Lookup.Id), nameof(Lookup.Name), Input.ExchangeId);
    public SelectList MarketItems => new(Lookups.Markets, nameof(Lookup.Id), nameof(Lookup.Name), Input.MarketId);
    public SelectList TimeFrameItems => new(Lookups.TimeFrames, nameof(TimeFrameLookup.Id), nameof(TimeFrameLookup.FriendlyName), Input.TimeFrameId);
    public SelectList SymbolItems => new(
        Lookups.Symbols.Select(s => new { s.Id, Label = string.IsNullOrWhiteSpace(s.Description) ? s.Name : $"{s.Name} ({s.Description})" }),
        "Id", "Label", Input.SymbolId);

    public string ExchangeName(int id) => Lookups.Exchanges.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";
    public string MarketName(int id) => Lookups.Markets.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";
    public string SymbolName(int id) => Lookups.Symbols.FirstOrDefault(x => x.Id == id)?.Name ?? $"#{id}";
    public string TimeFrameName(int id) => Lookups.TimeFrames.FirstOrDefault(x => x.Id == id)?.FriendlyName ?? $"#{id}";

    /// <summary>The Edit button links here with <c>?edit=id</c>, which fills the form with that series.</summary>
    public async Task<IActionResult> OnGetAsync(int? edit, CancellationToken cancellationToken)
    {
        var result = await LoadAsync(cancellationToken);
        if (result is not null)
            return result;

        if (edit is { } id)
        {
            var series = Series.FirstOrDefault(s => s.Id == id);
            if (series is null)
                Errors = ["That series does not exist (anymore)."];
            else
                Input = new SeriesForm
                {
                    Id = series.Id,
                    ExchangeId = series.ExchangeId,
                    MarketId = series.MarketId,
                    SymbolId = series.SymbolId,
                    TimeFrameId = series.TimeFrameId,
                    StartDate = series.StartDate
                };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            // The date input has no time or zone; the API reads a "Z" date as UTC, which is what is meant.
            var request = new SeriesRequest(
                Input.ExchangeId, Input.MarketId, Input.SymbolId, Input.TimeFrameId,
                DateTime.SpecifyKind(Input.StartDate!.Value.Date, DateTimeKind.Utc));

            var result = await CallAsync(() => IsEditing
                ? api.UpdateSeriesAsync(Input.Id, request, cancellationToken)
                : api.AddSeriesAsync(request, cancellationToken));

            if (result.SessionRejected)
                return await EndSessionAsync();

            if (result.Success)
            {
                Saved = IsEditing ? "The series was changed." : "The series was added.";
                Input = new SeriesForm();
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
        var result = await CallAsync(() => api.DeleteSeriesAsync(id, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = "The series was removed.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
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
            var series = await api.GetSeriesAsync(cancellationToken);
            if (series is null)
                return await EndSessionAsync();

            Series = series.OrderBy(s => s.Id).ToList();
            Lookups = await api.GetLookupsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            Errors = [.. Errors, "The API is not reachable right now."];
        }

        return null;
    }
}
