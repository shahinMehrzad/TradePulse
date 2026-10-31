using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Health;

public class CandleGapsHealthCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly OpenGaps _gaps = new();

    private static CandleGap Gap(int day, int fromHour, int toHour) =>
        new(new DateTime(2021, 8, day, fromHour, 0, 0, DateTimeKind.Utc), new DateTime(2021, 8, day, toHour, 0, 0, DateTimeKind.Utc));

    private static string Table(SeededSymbol symbol) => TestPlans.Binance(MarketTypeEnum.Futures, symbol, TimeFrameEnum._1H).Series.TableName;

    private Task<HealthCheckResult> RunAsync(params SeededSymbol[] collected) => RunWith(collected).ContinueWith(t => t.Result, Ct);

    private async Task<HealthCheckResult> RunWith(SeededSymbol[] collected)
    {
        using var provider = await CandleSeriesTestSupport.LoadedProviderAsync(
            _time, collected.Select(s => TestPlans.Binance(MarketTypeEnum.Futures, s, TimeFrameEnum._1H)).ToArray());
        return await new CandleGapsHealthCheck(_gaps, provider).CheckHealthAsync(new HealthCheckContext(), Ct);
    }

    // ---- the registry ----

    [Fact]
    public void ThePeriodsAreCountedInCandlesAndTheOldestIsKept()
    {
        _gaps.Set("t", [Gap(13, 2, 5), Gap(10, 7, 7)], Hour);   // 4 candles + 1 candle

        Assert.Equal(new GapSummary(2, 5, new DateTime(2021, 8, 10, 7, 0, 0, DateTimeKind.Utc)), _gaps.Get("t"));
    }

    [Fact]
    public void ATableThatWasLookedAtAndIsCompleteIsNotTheSameAsOneThatWasNever()
    {
        _gaps.Set("complete", [], Hour);

        Assert.Equal(new GapSummary(0, 0, null), _gaps.Get("complete"));
        Assert.Null(_gaps.Get("never-looked-at"));
    }

    [Fact]
    public void ANewRepairReplacesTheOldAnswer()
    {
        _gaps.Set("t", [Gap(13, 2, 5)], Hour);
        _gaps.Set("t", [], Hour);

        Assert.Equal(0, _gaps.Get("t")!.Value.Gaps);
    }

    // ---- the check ----

    [Fact]
    public async Task BeforeAnyRepairHasFinishedItSaysSoAndIsHealthy()
    {
        var result = await RunWith([SeededSymbol.BTCUSDT]);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("not finished", result.Description);
    }

    [Fact]
    public async Task CompleteTablesAreHealthyAndSaySo()
    {
        _gaps.Set(Table(SeededSymbol.BTCUSDT), [], Hour);
        _gaps.Set(Table(SeededSymbol.ETHUSDT), [], Hour);

        var result = await RunWith([SeededSymbol.BTCUSDT, SeededSymbol.ETHUSDT]);

        Assert.Equal((HealthStatus.Healthy, "No open gaps in 2 series"), (result.Status, result.Description));
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task GapsAreStillHealthyBecauseTheyAreTheExchangesAndTheyAreListedWithWhereTheyStart()
    {
        _gaps.Set(Table(SeededSymbol.BTCUSDT), [Gap(13, 2, 5), Gap(29, 7, 8)], Hour);
        _gaps.Set(Table(SeededSymbol.ETHUSDT), [], Hour);

        var result = await RunWith([SeededSymbol.BTCUSDT, SeededSymbol.ETHUSDT]);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("1 of 2 series have gaps the exchange has no candles for (6 candles missing)", result.Description);
        Assert.Equal("2 gap(s), 6 candle(s) missing, from 2021-08-13 02:00", result.Data[Table(SeededSymbol.BTCUSDT)]);
        Assert.DoesNotContain(Table(SeededSymbol.ETHUSDT), result.Data.Keys);
    }

    [Fact]
    public async Task ASeriesThatStartsAfterItsStartDateIsNamedApartAndIsNotCountedAsMissingCandles()
    {
        _gaps.Set(Table(SeededSymbol.BTCUSDT), [], Hour, dataStartsAt: new DateTime(2023, 5, 3, 16, 0, 0, DateTimeKind.Utc));

        var result = await RunWith([SeededSymbol.BTCUSDT]);

        Assert.Equal("No open gaps in 1 series", result.Description);
        Assert.Equal($"1 series start after their start date: {Table(SeededSymbol.BTCUSDT)} from 2023-05-03", result.Data["listed-later"]);
    }

    [Fact]
    public async Task SeriesNotCheckedYetAreCountedButDoNotHideTheOnesThatWere()
    {
        _gaps.Set(Table(SeededSymbol.BTCUSDT), [Gap(13, 2, 5)], Hour);

        var result = await RunWith([SeededSymbol.BTCUSDT, SeededSymbol.ETHUSDT]);

        Assert.EndsWith("(1 not checked yet)", result.Description);
        Assert.Contains(Table(SeededSymbol.BTCUSDT), result.Data.Keys);
    }

    [Fact]
    public async Task OnlyTheSeriesBeingCollectedNowAreListed()
    {
        _gaps.Set(Table(SeededSymbol.BTCUSDT), [Gap(13, 2, 5)], Hour);
        _gaps.Set(Table(SeededSymbol.XRPUSDT), [Gap(13, 2, 5)], Hour);   // removed from the configuration since

        var result = await RunWith([SeededSymbol.BTCUSDT]);

        Assert.Equal(Table(SeededSymbol.BTCUSDT), Assert.Single(result.Data).Key);
    }

    [Fact]
    public async Task AtMostTenSeriesAreListedOldestFirstAndTheRestIsCounted()
    {
        var symbols = Enum.GetValues<SeededSymbol>().Take(12).ToArray();
        for (var i = 0; i < symbols.Length; i++)
            _gaps.Set(Table(symbols[i]), [Gap(1 + i, 2, 3)], Hour);   // the later the symbol, the later its gap

        var result = await RunWith(symbols);

        Assert.Equal(11, result.Data.Count);                                  // ten series and "more"
        Assert.Equal("2 more series", result.Data["more"]);
        Assert.Contains(Table(symbols[0]), result.Data.Keys);                 // the oldest is there
        Assert.DoesNotContain(Table(symbols[11]), result.Data.Keys);          // the newest is not
    }
}
