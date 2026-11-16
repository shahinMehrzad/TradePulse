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

    // New York's week: opens Sunday 17:00 local, closes Friday 17:00 local. Summer time (UTC-4) from 2026-03-08 to 2026-11-01.
    private static readonly TradingHours NewYorkWeek = new(
        [(TimeSpan.FromHours(17), TimeSpan.FromDays(5) + TimeSpan.FromHours(17))], TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    [Theory]
    [InlineData(2026, 10, 18, 21)]   // summer time: 17:00 local is 21:00 UTC
    [InlineData(2026, 11, 8, 22)]    // winter time: 17:00 local is 22:00 UTC
    public void TheSameSessionIsAnHourEarlierInUtcInSummerTime(int year, int month, int day, int openUtcHour)
    {
        // The Sunday in question and the five days that follow it.
        var sunday = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);

        var first = NewYorkWeek.OpenRanges(sunday, sunday.AddDays(2)).First();

        Assert.Equal(sunday.AddHours(openUtcHour), first.Start);
    }

    [Fact]
    public void AWeekThatHasTheClocksChangeInItKeepsItsWallClockSessions()
    {
        // The week of 2026-11-01 (clocks back at 02:00 on Sunday): it opens the Sunday before at 17:00 EDT = 21:00 UTC and closes Friday 17:00 EST = 22:00 UTC.
        var ranges = NewYorkWeek.OpenRanges(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc)).ToList();

        Assert.Equal((new DateTime(2026, 10, 25, 21, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 30, 21, 0, 0, DateTimeKind.Utc)), ranges[0]);
        Assert.Equal((new DateTime(2026, 11, 1, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 6, 22, 0, 0, DateTimeKind.Utc)), ranges[1]);
    }

    [Fact]
    public void ASessionStartingInTheHourThatDoesNotExistStartsTheHourAfter()
    {
        // 2026-03-08: at 02:00 New York the clocks go forward to 03:00. A session that opens at Sunday 02:30 local starts at 03:30 local = 07:30 UTC.
        var hours = new TradingHours([(TimeSpan.FromHours(2.5), TimeSpan.FromHours(6))], TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        var first = hours.OpenRanges(new DateTime(2026, 3, 8, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)).First();

        Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc), first.Start);
    }

    // The weekly sessions the broker gave for EURUSD (2026-11-16): every day shut for two minutes (16:59 to 17:01 New York), Friday from 16:55.
    private static readonly TradingHours BrokerWeek = new(
    [
        (TimeSpan.FromHours(17) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(1) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(59)),
        (TimeSpan.FromDays(1) + TimeSpan.FromHours(17) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(2) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(59)),
        (TimeSpan.FromDays(2) + TimeSpan.FromHours(17) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(3) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(59)),
        (TimeSpan.FromDays(3) + TimeSpan.FromHours(17) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(4) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(59)),
        (TimeSpan.FromDays(4) + TimeSpan.FromHours(17) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(5) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(55)),
    ], TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    [Fact]
    public void TheTwoMinutesTheMarketIsShutEveryDayAreNoGapInOneMinuteCandles()
    {
        var calendar = new MarketCalendar();
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, Eurusd, BrokerWeek);
        var series = Series(TimeFrameEnum._1M);

        // Monday 2026-11-09, winter time (UTC-5): shut from 16:59 to 17:01 New York = 21:59 to 22:01 UTC.
        var parts = calendar.ExpectedPart(series, new CandleGap(new DateTime(2026, 11, 9, 21, 56, 0, DateTimeKind.Utc), new DateTime(2026, 11, 9, 22, 4, 0, DateTimeKind.Utc)));

        Assert.Equal(
        [
            new CandleGap(new DateTime(2026, 11, 9, 21, 56, 0, DateTimeKind.Utc), new DateTime(2026, 11, 9, 21, 58, 0, DateTimeKind.Utc)),
            new CandleGap(new DateTime(2026, 11, 9, 22, 1, 0, DateTimeKind.Utc), new DateTime(2026, 11, 9, 22, 4, 0, DateTimeKind.Utc)),
        ], parts);
    }

    [Fact]
    public void FridayClosesFiveMinutesEarlyAndTheWeekendIsOneBigHole()
    {
        var calendar = new MarketCalendar();
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, Eurusd, BrokerWeek);
        var series = Series(TimeFrameEnum._1M);

        // Friday 2026-11-13 16:55 New York is 21:55 UTC; the market opens again on Sunday 2026-11-15 at 17:01 New York = 22:01 UTC.
        var parts = calendar.ExpectedPart(series, new CandleGap(new DateTime(2026, 11, 13, 21, 52, 0, DateTimeKind.Utc), new DateTime(2026, 11, 15, 22, 3, 0, DateTimeKind.Utc)));

        Assert.Equal(
        [
            new CandleGap(new DateTime(2026, 11, 13, 21, 52, 0, DateTimeKind.Utc), new DateTime(2026, 11, 13, 21, 54, 0, DateTimeKind.Utc)),
            new CandleGap(new DateTime(2026, 11, 15, 22, 1, 0, DateTimeKind.Utc), new DateTime(2026, 11, 15, 22, 3, 0, DateTimeKind.Utc)),
        ], parts);
    }

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    // Gold, as the broker gave it (2026-11-16): a break of an hour every day (17:00 to 18:01 New York), Friday from 16:55.
    private static TradingHours GoldWeek(params TradingClosure[] closures) => new TradingHours(
    [
        (TimeSpan.FromHours(18) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(1) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(59)),
        (TimeSpan.FromDays(1) + TimeSpan.FromHours(18) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(2) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(59)),
        (TimeSpan.FromDays(2) + TimeSpan.FromHours(18) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(3) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(59)),
        (TimeSpan.FromDays(3) + TimeSpan.FromHours(18) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(4) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(59)),
        (TimeSpan.FromDays(4) + TimeSpan.FromHours(18) + TimeSpan.FromMinutes(1), TimeSpan.FromDays(5) + TimeSpan.FromHours(16) + TimeSpan.FromMinutes(55)),
    ], TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).WithClosures(closures);

    [Fact]
    public void AnEarlyCloseShutsTheMarketForTheRestOfTheSessionNotOnlyToMidnight()
    {
        // Thanksgiving 2026-11-26: the broker lists 22:30 to 23:59 Moscow time, which is 19:30 to 20:59 UTC. The session that day lasts to 16:59 New York = 21:59 UTC.
        var thanksgiving = new TradingClosure(Utc(11, 26, 19, 30), Utc(11, 26, 21, 0), ThroughSession: true);
        var calendar = new MarketCalendar();
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("XAUUSD"), GoldWeek(thanksgiving));
        var series = Series(TimeFrameEnum._1M, "XAUUSD");

        var parts = calendar.ExpectedPart(series, new CandleGap(Utc(11, 26, 19, 25), Utc(11, 26, 22, 30)));

        // Only the minutes before the early close are expected; the hour of break after it is shut anyway, and so is the rest of the session.
        Assert.Equal([new CandleGap(Utc(11, 26, 19, 25), Utc(11, 26, 19, 29))], parts);
    }

    [Fact]
    public void AClosureThatDoesNotEndTheDayLeavesTheRestOfTheSessionOpen()
    {
        var maintenance = new TradingClosure(Utc(11, 25, 10, 0), Utc(11, 25, 11, 0));
        var calendar = new MarketCalendar();
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("XAUUSD"), GoldWeek(maintenance));

        var parts = calendar.ExpectedPart(Series(TimeFrameEnum._1H, "XAUUSD"), new CandleGap(Utc(11, 25, 8, 0), Utc(11, 25, 13, 0)));

        Assert.Equal([new CandleGap(Utc(11, 25, 8, 0), Utc(11, 25, 9, 0)), new CandleGap(Utc(11, 25, 11, 0), Utc(11, 25, 13, 0))], parts);
    }

    [Fact]
    public void AWholeDayHolidayTakesTheDayOutAndLeavesTheNextSessionAlone()
    {
        // 2026-12-25 is a Friday. The broker lists it in Bucharest time (UTC+2): 22:00 UTC on the 24th to 22:00 UTC on the 25th. The session that day (Thursday 18:01 New York to
        // Friday 16:55 New York) is 23:01 UTC on the 24th to 21:55 UTC on the 25th, so it goes entirely. The session before it ends at 21:59 UTC on the 24th, and the week's next
        // one opens on Sunday the 27th at 18:01 New York = 23:01 UTC.
        var holiday = new TradingClosure(new DateTime(2026, 12, 24, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 25, 22, 0, 0, DateTimeKind.Utc), ThroughSession: true);
        var calendar = new MarketCalendar();
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("XAUUSD"), GoldWeek(holiday));

        var parts = calendar.ExpectedPart(Series(TimeFrameEnum._1H, "XAUUSD"),
            new CandleGap(new DateTime(2026, 12, 24, 20, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 28, 1, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(
        [
            new CandleGap(new DateTime(2026, 12, 24, 20, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 24, 21, 0, 0, DateTimeKind.Utc)),
            new CandleGap(new DateTime(2026, 12, 27, 23, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 28, 1, 0, 0, DateTimeKind.Utc)),
        ], parts);
    }

    [Fact]
    public void ClosuresFarFromTheRangeAreIgnoredAndTheHoursCanBeGivenNoneAgain()
    {
        var far = new TradingClosure(Utc(1, 1, 0), Utc(1, 2, 0));
        var withClosure = GoldWeek(far);
        var range = new CandleGap(Utc(11, 25, 8, 0), Utc(11, 25, 13, 0));
        var calendar = new MarketCalendar();

        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("XAUUSD"), withClosure);
        Assert.Equal([range], calendar.ExpectedPart(Series(TimeFrameEnum._1H, "XAUUSD"), range));

        // WithClosures replaces what was there: no closures at all gives the plain week again.
        var holiday = new TradingClosure(Utc(11, 25, 0, 0), Utc(11, 26, 0, 0), ThroughSession: true);
        calendar.SetHours(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("XAUUSD"), withClosure.WithClosures([holiday]).WithClosures([]));
        Assert.Equal([range], calendar.ExpectedPart(Series(TimeFrameEnum._1H, "XAUUSD"), range));
    }
}
