using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;
using WebRazor.Display;

namespace WebRazor.Pages;

public class SymbolModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    /// <summary>How many of the newest candles the page covers: the chart draws them and the figures above it describe them.</summary>
    public const int ChartCandles = 80;

    public DashboardDetail Detail { get; private set; } = null!;

    public CandleChartModel? Chart { get; private set; }

    public string? Error { get; private set; }

    /// <param name="timeframe">e.g. "4h"; the API's default (the shortest collected) when absent.</param>
    public async Task<IActionResult> OnGetAsync(string exchange, string market, string symbol, string? timeframe, CancellationToken cancellationToken)
    {
        try
        {
            var (detail, sessionRejected) = await api.GetDashboardDetailAsync(exchange, market, symbol, timeframe, ChartCandles, cancellationToken);
            if (sessionRejected)
                return await EndSessionAsync();
            if (detail is null)
                return NotFound();

            Detail = detail;

            // The very candles the figures were summed over, so the chart and the numbers above it cannot disagree.
            if (detail.Current is { WindowStart: { } from, LastOpenTime: { } to })
                Chart = CandleChart.Build(
                    await api.GetCandlesAsync(detail.Exchange, detail.Market, detail.Symbol, detail.Current.Timeframe, from, to, cancellationToken),
                    detail.Current.Timeframe);
        }
        catch (HttpRequestException)
        {
            // The card is what the page is about: without it there is nothing to show but the reason.
            if (Detail is null)
                return RedirectToPage("/Error");

            Error = "The candles could not be loaded right now.";
        }

        return Page();
    }
}
