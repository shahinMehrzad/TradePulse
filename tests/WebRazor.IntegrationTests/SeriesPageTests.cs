using System.Net;

namespace WebRazor.IntegrationTests;

/// <summary>
/// The Series page, rendered by the real panel: the buttons of each row, the form, the sort headers and the filters. The cases are the ones
/// that broke without any test noticing: Edit and Remove lost their id once the page started carrying its view in every link.
/// </summary>
public class SeriesPageTests(PanelFactory panel) : IClassFixture<PanelFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StubApi Api { get; } = panel.Api.Also(a => a.Reset());

    private async Task<string> GetAsync(string address)
    {
        var response = await panel.SignedInClient().GetAsync(address, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    // ---- the buttons of a row -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRowHasAnEditLinkThatNamesItsSeries()
    {
        var html = await GetAsync("/Series");

        Assert.Equal(["/Series?edit=2", "/Series?edit=5"], Html.LinksNamed(html, "Edit"));
    }

    [Fact]
    public async Task EveryRemoveFormPostsTheIdOfItsRow()
    {
        var html = await GetAsync("/Series");

        var removes = Html.FormActions(html).Where(a => a.Contains("handler=Delete")).Select(Html.Query).ToList();

        Assert.Equal(["2", "5"], removes.Select(q => q["id"]));
    }

    [Fact]
    public async Task AUseFirstCandleFormPostsTheIdOfItsRowAndTheOneForAllPostsNone()
    {
        Api.LateStarts((2, "2020-01-01T00:00:00", "2020-09-01T00:00:00"));

        var html = await GetAsync("/Series");

        var aligns = Html.FormActions(html).Where(a => a.Contains("handler=Align")).Select(Html.Query).ToList();
        Assert.Equal(2, aligns.Count);
        Assert.Contains(aligns, q => q.GetValueOrDefault("id") == "2");   // the row's own
        Assert.Contains(aligns, q => !q.ContainsKey("id"));               // "move all"
    }

    [Fact]
    public async Task TheLinksAndFormsKeepTheViewTheListIsShownIn()
    {
        var html = await GetAsync("/Series?market=2&sort=timeframe&dir=desc&size=25&oi=on&keyword=btc");

        var edit = Html.Query(Html.LinksNamed(html, "Edit")[0]);
        Assert.Equal("2", edit["edit"]);
        Assert.Equal(("timeframe", "desc", "25", "2", "on", "btc"), (edit["sort"], edit["dir"], edit["size"], edit["market"], edit["oi"], edit["keyword"]));

        // Removing, moving a start date and saving all come back to the same view too.
        foreach (var action in Html.FormActions(html).Where(a => a.StartsWith("/Series?")))
        {
            var query = Html.Query(action);
            Assert.Equal(("timeframe", "25", "2"), (query["sort"], query["size"], query["market"]));
        }
    }

    // ---- the form -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EditingFillsTheFormWithTheSeriesThatWasChosen()
    {
        var html = await GetAsync("/Series?edit=5");

        Assert.Contains("Change series", html);
        Assert.Contains("name=\"Input.Id\"", html);
        Assert.Contains("value=\"5\"", html);
        Assert.Equal("Binance", Html.SelectedOption(html, "Input.ExchangeId"));
        Assert.Equal("Futures", Html.SelectedOption(html, "Input.MarketId"));
        Assert.Equal("BTC/USDT (Bitcoin)", Html.SelectedOption(html, "Input.SymbolId"));
        Assert.Equal("1h", Html.SelectedOption(html, "Input.TimeFrameId"));
        Assert.Contains("value=\"2021-06-01\"", html);
    }

    [Fact]
    public async Task WithoutEditTheFormAddsANewSeries()
    {
        var html = await GetAsync("/Series");

        Assert.Contains("Add a series", html);
        Assert.DoesNotContain("Change series", html);
    }

    [Fact]
    public async Task EditingASeriesThatIsNotThereSaysSoInsteadOfFillingTheForm()
    {
        var html = await GetAsync("/Series?edit=999");

        Assert.Contains("That series does not exist", html);
        Assert.Contains("Add a series", html);
    }

    // ---- sorting ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClickOnAHeaderSortsByItAndASecondClickTurnsTheOrderRound()
    {
        var unsorted = Html.Query(Html.LinksNamed(await GetAsync("/Series"), "Time frame")[0]);
        var ascending = Html.Query(Html.LinksNamed(await GetAsync("/Series?sort=timeframe"), "Time frame")[0]);
        var descending = Html.Query(Html.LinksNamed(await GetAsync("/Series?sort=timeframe&dir=desc"), "Time frame")[0]);

        Assert.Equal(("timeframe", false), (unsorted["sort"], unsorted.ContainsKey("dir")));   // first click: ascending
        Assert.Equal("desc", ascending["dir"]);                                                  // second: the other way round
        Assert.False(descending.ContainsKey("dir"));                                             // third: ascending again
    }

    [Fact]
    public async Task TheSortedColumnShowsWhichWayItIsSorted()
    {
        var html = await GetAsync("/Series?sort=symbol&dir=desc");

        Assert.Single(Html.LinksNamed(html, "Symbol ↓"));
        Assert.Empty(Html.LinksNamed(html, "Symbol ↑"));
    }

    [Fact]
    public async Task ASortingClickStartsAgainAtTheFirstPage()
    {
        Api.SeriesOf(25, StubApi.Row(1, 900, "2020-01-01T00:00:00", false, false));

        var html = await GetAsync("/Series?pageNumber=3&size=10");

        Assert.False(Html.Query(Html.LinksNamed(html, "Exchange")[0]).ContainsKey("pageNumber"));
    }

    // ---- what reaches the API -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("?sort=nonsense", "$top=10")]
    [InlineData("?size=7", "$top=10")]
    [InlineData("?size=100", "$top=100")]
    [InlineData("?pageNumber=-4", "$skip=0")]
    [InlineData("?pageNumber=3&size=25", "$skip=50")]
    public async Task WhatTheAddressHoldsThatTheApiWouldRefuseNeverReachesIt(string query, string expected)
    {
        await GetAsync("/Series" + query);

        var asked = Api.Requests.Last(r => r.Contains("candle-worker/odata"));
        Assert.Contains(expected, asked);
        Assert.DoesNotContain("nonsense", asked);
    }

    [Fact]
    public async Task TheFiltersAndTheSortAreAskedOfTheApiAsOneQuery()
    {
        await GetAsync("/Series?market=2&oi=on&sort=timeframe&dir=desc");

        var asked = Uri.UnescapeDataString(Api.Requests.Last(r => r.Contains("candle-worker/odata")));
        Assert.Contains("$orderby=intervalSeconds desc,symbol,id", asked);   // the sorted column, then what the list was always sorted by
        Assert.Contains("marketId eq 2 and collectOpenInterest eq true", asked);
    }

    [Fact]
    public async Task TheFilterBoxesShowWhatTheListIsFilteredBy()
    {
        var html = await GetAsync("/Series?market=2&timeframe=3600&oi=on&stats=off&size=50");

        Assert.Equal("Futures", Html.SelectedOption(html, "market"));
        Assert.Equal("1h", Html.SelectedOption(html, "timeframe"));
        Assert.Equal("On", Html.SelectedOption(html, "oi"));
        Assert.Equal("Off", Html.SelectedOption(html, "stats"));
        Assert.Equal("50", Html.SelectedOption(html, "size"));
    }

    [Fact]
    public async Task TheListSaysHowManyMatchAndNoMatchIsNotTheEmptyListMessage()
    {
        Api.SeriesOf(0);

        var html = await GetAsync("/Series?market=1");

        Assert.Contains("No series match the search and the filters.", html);
        Assert.DoesNotContain("Nothing is configured yet", html);
    }

    // ---- paging ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePagerKeepsTheViewAndMovesOnePageEitherWay()
    {
        Api.SeriesOf(60, StubApi.Row(1, 900, "2020-01-01T00:00:00", false, false));

        // 60 rows of 25: three pages. (A size of 10, the default, is left out of the address on purpose.)
        var html = await GetAsync("/Series?pageNumber=2&size=25&market=2&sort=symbol");

        var next = Html.Query(Html.LinksNamed(html, "Next")[0]);
        var previous = Html.Query(Html.LinksNamed(html, "Previous")[0]);
        Assert.Equal(("3", "25", "2", "symbol"), (next["pageNumber"], next["size"], next["market"], next["sort"]));
        Assert.False(previous.ContainsKey("pageNumber"));   // page 1 has a clean address
        Assert.Equal("symbol", previous["sort"]);
    }
}

internal static class Fluent
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
