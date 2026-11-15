using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace Domain.UnitTests;

public class MarketCalendarTests
{
    private static readonly ExchangeSymbol Eurusd = new("EURUSD");

    // A forex-like week: opens on Sunday at 22:00 UTC, closes on Friday at 22:00 UTC.
    private static readonly TradingHours ForexWeek = new([(TimeSpan.FromHours(22), TimeSpan.FromDays(5) + TimeSpan.FromHours(22))]);

    private static CandleSeries Series(TimeFrameEnum timeFrame = TimeFrameEnum._1H, string symbol = "EURUSD") =>
        new(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol(symbol), timeFrame);

    private static MarketCalendar WithForexHours()
    {
        var calendar = new MarketCalendar();
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, Eurusd, ForexWeek);
        return calendar;
    }

    // 2026-11-13 is a Friday, 2026-11-15 a Sunday.
    private static DateTime At(int day, int hour) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASymbolWithoutHoursIsAlwaysOpen()
    {
        var gap = new CandleGap(At(14, 0), At(15, 20));

        Assert.Equal([gap], new MarketCalendar().ExpectedPart(Series(), gap));
        Assert.Equal([gap], MarketCalendar.AlwaysOpen.ExpectedPart(Series(), gap));
    }

    [Fact]
    public void AWeekendWithNoCandlesIsNoGap()
    {
        // Friday 22:00 to Sunday 21:00, the hour candles of the closed weekend.
        Assert.Empty(WithForexHours().ExpectedPart(Series(), new CandleGap(At(13, 22), At(15, 21))));
    }

    [Fact]
    public void OnlyTheOpenPartsOfAGapAcrossTheWeekendAreExpected()
    {
        var parts = WithForexHours().ExpectedPart(Series(), new CandleGap(At(13, 20), At(16, 2)));

        Assert.Equal([new CandleGap(At(13, 20), At(13, 21)), new CandleGap(At(15, 22), At(16, 2))], parts);
    }

    [Fact]
    public void AGapInsideOpenTimeComesBackWhole()
    {
        var gap = new CandleGap(At(10, 3), At(11, 17));

        Assert.Equal([gap], WithForexHours().ExpectedPart(Series(), gap));
    }

    [Fact]
    public void ACandleThatIsOpenForPartOfItsTimeIsExpected()
    {
        // 4 hour candles: the one opening on Sunday at 20:00 runs until midnight and the market opens at 22:00 inside it.
        var parts = WithForexHours().ExpectedPart(Series(TimeFrameEnum._4H), new CandleGap(At(15, 12), At(15, 20)));

        Assert.Equal([new CandleGap(At(15, 20), At(15, 20))], parts);
    }

    [Fact]
    public void TheRunBeforeTheFirstCandleIsTheStartOfTheSeriesAndStaysAsItIs()
    {
        var gap = new CandleGap(At(13, 22), At(15, 21), BeforeFirstCandle: true);

        Assert.Equal([gap], WithForexHours().ExpectedPart(Series(), gap));
    }

    [Fact]
    public void ASymbolIsFoundInAnyCaseAndOtherSymbolsAreNotAffected()
    {
        var calendar = WithForexHours();
        var weekend = new CandleGap(At(13, 22), At(15, 21));

        Assert.Empty(calendar.ExpectedPart(Series(symbol: "eurusd"), weekend));
        Assert.Equal([weekend], calendar.ExpectedPart(Series(symbol: "XAUUSD"), weekend));
    }

    [Fact]
    public void ForgettingTheHoursMakesTheSymbolAlwaysOpenAgain()
    {
        var calendar = WithForexHours();
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, Eurusd, null);
        var weekend = new CandleGap(At(13, 22), At(15, 21));

        Assert.Equal([weekend], calendar.ExpectedPart(Series(), weekend));
    }

    [Fact]
    public void SessionsThatTouchAreJoinedSoADayBoundaryIsNotAHole()
    {
        var hours = new TradingHours([(TimeSpan.FromDays(1), TimeSpan.FromDays(2)), (TimeSpan.FromDays(2), TimeSpan.FromDays(3))]);
        var calendar = new MarketCalendar();
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, Eurusd, hours);

        var gap = new CandleGap(At(16, 0), At(17, 23));   // Monday and Tuesday

        Assert.Equal([gap], calendar.ExpectedPart(Series(), gap));
    }

    [Fact]
    public void ALongGapIsAnsweredFast()
    {
        // Years of one minute candles: the answer comes from the weeks, not from the candles.
        var gap = new CandleGap(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        var parts = WithForexHours().ExpectedPart(Series(TimeFrameEnum._1M), gap);

        Assert.InRange(parts.Count, 300, 400);   // about one per week
    }

    [Theory]
    [InlineData(-1, 60)]
    [InlineData(0, 0)]
    [InlineData(160, 170)]   // 170 hours: more than the 168 of a week
    public void SessionsMustLieInsideTheWeekAndEndAfterTheyStart(int startHours, int endHours)
    {
        Assert.Throws<ArgumentException>(() => new TradingHours([(TimeSpan.FromHours(startHours), TimeSpan.FromHours(endHours))]));
    }

    [Fact]
    public void AMarketThatIsNeverOpenHasNoHours()
    {
        Assert.Throws<ArgumentException>(() => new TradingHours([]));
    }
}
