using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using WebRazor.Api;

namespace WebRazor.Pages;

public class SeriesModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public IReadOnlyList<CandleWorkerSeries> Series { get; private set; } = [];

    // What the list shows is in the address, so a page can be bookmarked, reloaded or sent to somebody, and the edit, save and remove round
    // trips come back to the same view. Every name is cleaned in SeriesListQuery.From; an unknown value is simply the default.

    /// <summary>Which page of the list is shown.</summary>
    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Narrows the list; the API matches it against the names of the exchange, market, symbol and time frame.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Keyword { get; set; }

    /// <summary>How many rows a page holds: one of <see cref="SeriesListQuery.PageSizes"/>.</summary>
    [BindProperty(Name = "size", SupportsGet = true)]
    public int? Size { get; set; }

    /// <summary>The column the list is sorted by, a key of <see cref="SeriesListQuery.SortFields"/>.</summary>
    [BindProperty(Name = "sort", SupportsGet = true)]
    public string? Sort { get; set; }

    /// <summary><c>desc</c> for the other way round.</summary>
    [BindProperty(Name = "dir", SupportsGet = true)]
    public string? Direction { get; set; }

    [BindProperty(Name = "exchange", SupportsGet = true)]
    public int? ExchangeFilter { get; set; }

    [BindProperty(Name = "market", SupportsGet = true)]
    public int? MarketFilter { get; set; }

    [BindProperty(Name = "timeframe", SupportsGet = true)]
    public int? TimeFrameFilter { get; set; }

    /// <summary><c>on</c> or <c>off</c>; anything else is either.</summary>
    [BindProperty(Name = "oi", SupportsGet = true)]
    public string? OpenInterestFilter { get; set; }

    /// <summary><c>on</c> or <c>off</c>; anything else is either.</summary>
    [BindProperty(Name = "stats", SupportsGet = true)]
    public string? StatisticsFilter { get; set; }

    /// <summary>The cleaned query: what is really asked of the API and what the links carry on.</summary>
    public SeriesListQuery List { get; private set; } = new();

    public bool HasPrevious => List.PageNumber > 1;

    public bool HasNext { get; private set; }

    /// <summary>How many series match the search and the filters (all of them without any), as the API counts them.</summary>
    public int TotalItems { get; private set; }

    public int TotalPages { get; private set; }

    public Lookups Lookups { get; private set; } = Lookups.Empty;

    /// <summary>The series that start later than their start date, by id: every series, not only this page.</summary>
    public IReadOnlyDictionary<int, LateStart> LateStarts { get; private set; } = new Dictionary<int, LateStart>();

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

        /// <summary>Also collect the open interest of this series. The API refuses it where the market has none.</summary>
        public bool CollectOpenInterest { get; set; }

        /// <summary>Also collect the long/short ratio and taker volume of this series.</summary>
        public bool CollectMarketStatistics { get; set; }
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

    /// <summary>Whether the series the form describes right now can carry open interest (the checkbox is disabled when not).</summary>
    public bool OpenInterestAvailable => Lookups.Supports(Input.ExchangeId, Input.MarketId, "OpenInterest", Input.TimeFrameId);

    /// <summary>Says where open interest exists, from the rules the API publishes.</summary>
    public string OpenInterestRule
    {
        get
        {
            var entries = Lookups.MarketDataSupport.Where(s => s.DataType == "OpenInterest" && s.TimeFrameIds is not null).ToList();
            if (entries.Count == 0)
                return "Not available on any market.";

            var markets = string.Join(" and ", entries.Select(e => MarketName(e.MarketId)).Distinct());
            var ids = entries.SelectMany(e => e.TimeFrameIds!).ToHashSet();
            var frames = Lookups.TimeFrames.Where(t => ids.Contains(t.Id)).OrderBy(t => t.IntervalToSeconds).Select(t => t.FriendlyName);
            return $"{markets} only, at {string.Join(", ", frames)}.";
        }
    }

    /// <summary>Whether the series the form describes right now can carry market statistics (the checkbox is disabled when not).</summary>
    public bool MarketStatisticsAvailable =>
        Lookups.Supports(Input.ExchangeId, Input.MarketId, "LongShortRatio", Input.TimeFrameId)
        && Lookups.Supports(Input.ExchangeId, Input.MarketId, "TakerVolume", Input.TimeFrameId);

    /// <summary>The Edit button links here with <c>?edit=id</c>, which fills the form with that series.</summary>
    public async Task<IActionResult> OnGetAsync(int? edit, CancellationToken cancellationToken)
    {
        // A hand-edited address must not reach the API with a page or a sort it would refuse.
        Clean();

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
                    StartDate = series.StartDate,
                    CollectOpenInterest = series.CollectOpenInterest,
                    CollectMarketStatistics = series.CollectMarketStatistics
                };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        Clean();

        if (ModelState.IsValid)
        {
            // The date input has no time or zone; the API reads a "Z" date as UTC, which is what is meant.
            var request = new SeriesRequest(
                Input.ExchangeId, Input.MarketId, Input.SymbolId, Input.TimeFrameId,
                DateTime.SpecifyKind(Input.StartDate!.Value.Date, DateTimeKind.Utc),
                Input.CollectOpenInterest,
                Input.CollectMarketStatistics);

            var result = await CallAsync(() => IsEditing
                ? api.UpdateSeriesAsync(Input.Id, request, cancellationToken)
                : api.AddSeriesAsync(request, cancellationToken));

            if (result.SessionRejected)
                return await EndSessionAsync();

            if (result.Success)
            {
                Saved = IsEditing ? "The series was changed." : "The series was added.";
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
        Clean();

        var result = await CallAsync(() => api.DeleteSeriesAsync(id, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = "The series was removed.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>Moves the start date of one series (or of all that start late) to its first candle. Only on request.</summary>
    public async Task<IActionResult> OnPostAlignAsync(int? id, CancellationToken cancellationToken)
    {
        ResetForm();
        Clean();

        var result = await CallAsync(() => api.AlignStartsAsync(id, cancellationToken));
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
            Saved = id is null ? "The start dates were moved to the first candle." : "The start date was moved to the first candle.";
        else
            Errors = result.Errors;

        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>Reads the view (search, filters, sort, page, size) from the address and cleans it.</summary>
    private void Clean()
    {
        List = SeriesListQuery.From(PageNumber, Size, Keyword, Sort, Direction, ExchangeFilter, MarketFilter, TimeFrameFilter,
            OpenInterestFilter, StatisticsFilter);
        PageNumber = List.PageNumber;
    }

    /// <summary>The address values that every link and form of the page carries, so that the view survives them.</summary>
    public IDictionary<string, string> ListRoute => List.RouteValues();

    /// <summary>
    /// The address values of the view plus one more (<c>edit</c>, <c>id</c>). They go in the same dictionary on purpose: a tag helper that is
    /// given <c>asp-all-route-data</c> silently drops every <c>asp-route-xxx</c> next to it, which is how the Edit and Remove buttons lost their id.
    /// </summary>
    public IDictionary<string, string> WithValue(string key, int value)
    {
        var values = List.RouteValues();
        values[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return values;
    }

    /// <summary>The address of a click on a column header (that column first, the other way round when it is already the sorted one).</summary>
    public IDictionary<string, string> SortRoute(string key) => List.SortedBy(key).RouteValues();

    /// <summary>The address of another page of the same view.</summary>
    public IDictionary<string, string> PageRoute(int pageNumber) => List.OnPage(pageNumber).RouteValues();

    /// <summary>The arrow in a column header: which way the list is sorted by it, nothing for the others.</summary>
    public string SortMark(string key) => List.SortBy != key ? "" : List.Descending ? " ↓" : " ↑";

    /// <summary>The value the page-size and filter forms put back, for the select that holds it.</summary>
    public bool IsPageSize(int size) => List.PageSize == size;

    /// <summary>
    /// Back to an empty "add" form. The posted values have to go too: a tag helper shows what was posted (the hidden id, the old
    /// choices) in preference to the model, so resetting <see cref="Input"/> alone would leave the form still pointing at the old row.
    /// </summary>
    private void ResetForm()
    {
        ModelState.Clear();
        Input = new SeriesForm();
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

    /// <summary>A note, not the list itself: when it cannot be had the page is shown without it.</summary>
    private async Task<IReadOnlyDictionary<int, LateStart>> LoadLateStartsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await api.GetLateStartsAsync(cancellationToken) ?? []).ToDictionary(l => l.Id);
        }
        catch (HttpRequestException)
        {
            return new Dictionary<int, LateStart>();
        }
    }

    /// <returns>A redirect when the session is over, otherwise null (the page then renders with whatever could be loaded).</returns>
    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var page = await api.GetSeriesAsync(List, cancellationToken);

            // Removing the last row of the last page leaves that page empty: step back to the one before it.
            while (page is { Items.Count: 0 } && List.PageNumber > 1)
            {
                List = List.OnPage(List.PageNumber - 1);
                page = await api.GetSeriesAsync(List, cancellationToken);
            }

            if (page is null)
                return await EndSessionAsync();

            Series = page.Items;
            HasNext = page.HasNext;
            TotalItems = page.TotalItems;
            TotalPages = page.TotalPages;
            Lookups = await api.GetLookupsAsync(cancellationToken);
            LateStarts = await LoadLateStartsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            Errors = [.. Errors, "The API is not reachable right now."];
        }

        return null;
    }
}
