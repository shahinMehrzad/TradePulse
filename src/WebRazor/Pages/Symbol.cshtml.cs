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

    /// <summary>How many periods of long/short ratio and taker volume the two charts show.</summary>
    public const int StatisticsPoints = 48;

    /// <summary>The long/short ratio and taker volume of this series; null for spot, or when they could not be loaded.</summary>
    public MarketStatistics? Statistics { get; private set; }

    public LineChartModel? LongShortChart { get; private set; }

    public LineChartModel? TakerChart { get; private set; }

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

        // Futures only (spot has none), and a side note like the related data: when it cannot be had the page is shown without it.
        if (Detail is { Market: "futures" })
        {
            try
            {
                var (statistics, sessionRejected) = await api.GetMarketStatisticsAsync(
                    Detail.Exchange, Detail.Market, Detail.Symbol, Detail.Current.Timeframe, StatisticsPoints, cancellationToken);
                if (sessionRejected)
                    return await EndSessionAsync();

                Statistics = statistics;
                if (statistics is { Collected: true })
                {
                    LongShortChart = LineChart.Build([.. statistics.LongShort.Select(p => (p.Time, p.Ratio))], statistics.Timeframe, "0.000", reference: 1m,
                        label: "Long to short account ratio");
                    TakerChart = LineChart.Build([.. statistics.TakerVolume.Select(p => (p.Time, p.BuySellRatio))], statistics.Timeframe, "0.000", reference: 1m,
                        label: "Taker buy to sell volume ratio");
                }
            }
            catch (HttpRequestException)
            {
                Statistics = null;
            }
        }

        return Page();
    }
}
