using System.Net;
using System.Text.RegularExpressions;

namespace WebRazor.IntegrationTests;

/// <summary>The page of one symbol: the cards of the long/short ratio and the taker volume, rendered by the real panel from what the API says.</summary>
public class SymbolPageTests(PanelFactory panel) : IClassFixture<PanelFactory>
{
    private const string Futures = "/api/admin/dashboard/series/binance/futures/btcusdt";

    private static readonly DateTime Start = new(2026, 10, 28, 16, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StubApi Api { get; } = panel.Api.Also(a => a.Reset());

    private async Task<string> GetAsync(string address)
    {
        var response = await panel.SignedInClient().GetAsync(address, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>A symbol with no candles stored (so the page has no candle chart to fetch and the cards are what is left to look at).</summary>
    private void Symbol(string market, string prefix)
    {
        Api.Answer(prefix, new
        {
            exchange = "binance", market, symbol = "btcusdt", timeframes = new[] { "1h", "4h" }, defaultTimeframe = "1h",
            current = new { timeframe = "4h", status = "empty" },
            related = Array.Empty<object>(),
        });
    }

    private void Statistics(bool collected, int periods)
    {
        var longShort = Enumerable.Range(0, periods).Select(i => new
        {
            time = Start.AddHours(4 * i), ratio = i == periods - 1 ? 1.414m : 1.2m + i % 5 / 10m, longAccount = i == periods - 1 ? 0.586m : 0.55m, shortAccount = i == periods - 1 ? 0.414m : 0.45m,
        });
        var taker = Enumerable.Range(0, periods).Select(i => new
        {
            time = Start.AddHours(4 * i), buySellRatio = i == periods - 1 ? 0.981m : 1m + i % 3 / 10m, buyVolume = i == periods - 1 ? 6304.41m : 6000m, sellVolume = i == periods - 1 ? 6429.50m : 6100m,
        });
        Api.Answer(Futures + "/statistics", new { exchange = "binance", market = "futures", symbol = "btcusdt", timeframe = "4h", collected, longShort, takerVolume = taker });
    }

    [Fact]
    public async Task AFuturesSeriesThatCollectsThemShowsBothCardsWithTheNewestValues()
    {
        Symbol("futures", Futures);
        Statistics(collected: true, periods: 48);

        var html = await GetAsync("/symbol/binance/futures/btcusdt?timeframe=4h");
        var text = Html.Text(html);

        Assert.Contains("Long / short accounts", text);
        Assert.Contains("1.414", text);
        Assert.Contains("Long 58.6%", text);
        Assert.Contains("Short 41.4%", text);
        Assert.Contains("Taker buy / sell volume", text);
        Assert.Contains("0.981", text);
        Assert.Contains("Bought 6,304.41", text);
        Assert.Contains("Sold 6,429.50", text);
        Assert.Equal(2, Regex.Matches(html, "<polyline").Count);   // one line chart in each card
    }

    [Fact]
    public async Task TheStatisticsAreAskedForTheTimeFrameThePageShows()
    {
        Symbol("futures", Futures);
        Statistics(collected: true, periods: 3);

        await GetAsync("/symbol/binance/futures/btcusdt?timeframe=4h");

        Assert.Contains(Api.Requests, r => r.Contains("/statistics") && r.Contains("timeframe=4h") && r.Contains("points=48"));
    }

    [Fact]
    public async Task TheFirstTimeOfAChartStartsAtItsLeftEdgeSoItIsNotCutOff()
    {
        Symbol("futures", Futures);
        Statistics(collected: true, periods: 48);

        var html = await GetAsync("/symbol/binance/futures/btcusdt?timeframe=4h");

        // The first label of each of the two charts is the oldest time (10-28 16:00), anchored at its start; the others are centred.
        var starts = Regex.Matches(html, "<text[^>]*text-anchor=\"start\"[^>]*>(?<label>[^<]*)</text>").Select(m => m.Groups["label"].Value).ToList();
        Assert.Equal(["10-28 16:00", "10-28 16:00"], starts);
    }

    [Fact]
    public async Task ASeriesThatDoesNotCollectThemShowsNoCardsAndNoError()
    {
        Symbol("futures", Futures);
        Statistics(collected: false, periods: 0);

        var text = Html.Text(await GetAsync("/symbol/binance/futures/btcusdt?timeframe=4h"));

        Assert.DoesNotContain("Long / short accounts", text);
        Assert.DoesNotContain("Taker buy / sell volume", text);
    }

    [Fact]
    public async Task ASeriesThatCollectsThemButHasNoRowsYetSaysSoInEachCard()
    {
        Symbol("futures", Futures);
        Statistics(collected: true, periods: 0);

        var text = Html.Text(await GetAsync("/symbol/binance/futures/btcusdt?timeframe=4h"));

        Assert.Contains("Long / short accounts", text);
        Assert.Equal(2, Regex.Matches(text, "Nothing is stored yet").Count);
    }

    [Fact]
    public async Task APageWhoseStatisticsCannotBeHadIsStillShown()
    {
        Symbol("futures", Futures);   // no answer for /statistics: the stub says 404

        var text = Html.Text(await GetAsync("/symbol/binance/futures/btcusdt?timeframe=4h"));

        Assert.Contains("BTCUSDT", text);
        Assert.DoesNotContain("Long / short accounts", text);
    }

    [Fact]
    public async Task SpotHasNoStatisticsAndTheApiIsNotAskedForThem()
    {
        Symbol("spot", "/api/admin/dashboard/series/binance/spot/btcusdt");

        var text = Html.Text(await GetAsync("/symbol/binance/spot/btcusdt?timeframe=4h"));

        Assert.DoesNotContain("Long / short accounts", text);
        Assert.DoesNotContain(Api.Requests, r => r.Contains("/statistics"));
    }
}
