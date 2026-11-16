using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;
using Google.Protobuf;
using Infrastructure.Exchange.Ctrader;
using Infrastructure.Exchange.Ctrader.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.UnitTests.Ctrader;

/// <summary>The candle gateway, the symbol catalog and the symbol directory behind them, against a scripted cTrader.</summary>
public class CtraderGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const long AccountId = 99;
    private const long EurUsdId = 1;
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CandleSeries EurUsdHourly = new(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("EURUSD"), TimeFrameEnum._1H);

    /// <summary>A cTrader that answers from lists the test fills, and remembers what it was asked.</summary>
    private sealed class ScriptedConnection : ICtraderConnection
    {
        public long AccountId => CtraderGatewayTests.AccountId;

        public List<ProtoOALightSymbol> Symbols { get; } = [new() { SymbolId = EurUsdId, SymbolName = "EURUSD", Enabled = true }];
        public ProtoOASymbol Details { get; set; } = new() { SymbolId = EurUsdId, Digits = 5, PipPosition = 4 };
        public Func<ProtoOAGetTrendbarsReq, ProtoOAGetTrendbarsRes> Trendbars { get; set; } = _ => new ProtoOAGetTrendbarsRes();
        public Exception? Failure { get; set; }

        public List<ProtoOAGetTrendbarsReq> TrendbarRequests { get; } = [];
        public int SymbolListRequests { get; private set; }
        public int DetailRequests { get; private set; }

        public Task<TResponse> RequestAsync<TResponse>(ProtoOAPayloadType type, IMessage request, bool historical, CancellationToken cancellationToken)
            where TResponse : class, IMessage<TResponse>, new()
        {
            if (Failure is not null)
                return Task.FromException<TResponse>(Failure);

            IMessage answer = request switch
            {
                ProtoOASymbolsListReq => Listed(),
                ProtoOASymbolByIdReq => Detailed(),
                ProtoOAGetTrendbarsReq r => Asked(r, historical),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)answer);
        }

        private ProtoOASymbolsListRes Listed()
        {
            SymbolListRequests++;
            return new ProtoOASymbolsListRes { CtidTraderAccountId = AccountId, Symbol = { Symbols } };
        }

        private ProtoOASymbolByIdRes Detailed()
        {
            DetailRequests++;
            return new ProtoOASymbolByIdRes { CtidTraderAccountId = AccountId, Symbol = { Details } };
        }

        private ProtoOAGetTrendbarsRes Asked(ProtoOAGetTrendbarsReq request, bool historical)
        {
            Assert.True(historical, "candle requests count against the historical limit");
            TrendbarRequests.Add(request);
            return Trendbars(request);
        }
    }

    private static ProtoOATrendbar Bar(DateTime open, long low, ulong deltaOpen, ulong deltaHigh, ulong deltaClose, long volume = 100) => new()
    {
        UtcTimestampInMinutes = (uint)(new DateTimeOffset(open, TimeSpan.Zero).ToUnixTimeSeconds() / 60),
        Low = low, DeltaOpen = deltaOpen, DeltaHigh = deltaHigh, DeltaClose = deltaClose, Volume = volume,
    };

    private static long Ms(DateTime time) => new DateTimeOffset(time, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static (CtraderCandleGateway Gateway, CtraderSymbolDirectory Symbols, FakeTimeProvider Time) Build(ScriptedConnection connection, MarketCalendar? calendar = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        var symbols = new CtraderSymbolDirectory(connection, time, NullLogger<CtraderSymbolDirectory>.Instance, calendar);
        return (new CtraderCandleGateway(connection, symbols, time), symbols, time);
    }

    [Fact]
    public async Task ABarIsDecodedFromItsRelativePricesAndRoundedToTheDigitsOfTheSymbol()
    {
        var open = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
        var connection = new ScriptedConnection
        {
            Trendbars = _ => new ProtoOAGetTrendbarsRes { Trendbar = { Bar(open, low: 108_345, deltaOpen: 10, deltaHigh: 55, deltaClose: 30, volume: 1234) } },
        };

        var result = await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly, open, null, Ct);

        var candle = Assert.Single(result.Value.Items);
        Assert.Equal(open, candle.TimeRange.OpenTime);
        Assert.Equal(open.AddHours(1).AddMilliseconds(-1), candle.TimeRange.CloseTime);
        Assert.Equal(1.08345m, candle.Low.Value);
        Assert.Equal(1.08355m, candle.Open.Value);
        Assert.Equal(1.08400m, candle.High.Value);
        Assert.Equal(1.08375m, candle.Close.Value);
        Assert.Equal(1234m, candle.Volume);
        Assert.Equal(0m, candle.QuoteAssetVolume);
        Assert.Equal(0, candle.NumberOfTrades);
    }

    [Fact]
    public async Task PricesAreRoundedToTheDigitsTheBrokerShows()
    {
        var open = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
        var connection = new ScriptedConnection
        {
            Details = new ProtoOASymbol { SymbolId = EurUsdId, Digits = 3 },
            Trendbars = _ => new ProtoOAGetTrendbarsRes { Trendbar = { Bar(open, low: 150_123, deltaOpen: 0, deltaHigh: 0, deltaClose: 0) } },
        };

        var candle = (await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly, open, null, Ct)).Value.Items.Single();

        Assert.Equal(1.501m, candle.Open.Value);
    }

    [Fact]
    public async Task TheRequestNamesTheAccountTheSymbolNumberThePeriodAndTheRangeInMilliseconds()
    {
        var connection = new ScriptedConnection();
        var from = new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);

        await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly with { Timeframe = TimeFrameEnum._30M }, from, to, Ct);

        var request = Assert.Single(connection.TrendbarRequests);
        Assert.Equal(AccountId, request.CtidTraderAccountId);
        Assert.Equal(EurUsdId, request.SymbolId);
        Assert.Equal(ProtoOATrendbarPeriod.M30, request.Period);
        Assert.Equal(Ms(from), request.FromTimestamp);
        Assert.Equal(Ms(to), request.ToTimestamp);
    }

    [Fact]
    public async Task WithoutAnEndTheRangeEndsNow()
    {
        var connection = new ScriptedConnection();

        await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-3), null, Ct);

        Assert.Equal(Ms(Now), connection.TrendbarRequests.Single().ToTimestamp);
    }

    [Fact]
    public async Task AStretchWithNoBarIsSkippedOverSoTheWorkerNeverTakesItForTheEnd()
    {
        // One minute bars: one request covers 3.5 days. Ten days from the start, the bars only begin in the third stretch.
        var from = Now.AddDays(-10);
        var barOpen = Now.AddDays(-2);
        var connection = new ScriptedConnection
        {
            // The bar is returned by the stretch that reaches it, the stretches before it are empty.
            Trendbars = r => r.ToTimestamp >= Ms(barOpen)
                ? new ProtoOAGetTrendbarsRes { Trendbar = { Bar(barOpen, 100_000, 0, 0, 0) } }
                : new ProtoOAGetTrendbarsRes(),
        };

        var result = await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly with { Timeframe = TimeFrameEnum._1M }, from, null, Ct);

        Assert.Single(result.Value.Items);
        Assert.Equal(3, connection.TrendbarRequests.Count);
        // The stretches follow each other without a hole and without asking for the same millisecond twice.
        Assert.Equal(connection.TrendbarRequests[0].ToTimestamp + 1, connection.TrendbarRequests[1].FromTimestamp);
        Assert.Equal(connection.TrendbarRequests[1].ToTimestamp + 1, connection.TrendbarRequests[2].FromTimestamp);
        Assert.All(connection.TrendbarRequests, r => Assert.True(r.ToTimestamp - r.FromTimestamp <= 302_400_000));
    }

    [Fact]
    public async Task APageSaysThereIsMoreWhenTheRangeWasCutShort()
    {
        var open = Now.AddDays(-9);
        var connection = new ScriptedConnection { Trendbars = _ => new ProtoOAGetTrendbarsRes { Trendbar = { Bar(open, 100_000, 0, 0, 0) } } };
        var gateway = Build(connection).Gateway;

        // The range is longer than one request may cover, so there is more even though cTrader said nothing.
        var cut = await gateway.GetCandlesAsync(EurUsdHourly with { Timeframe = TimeFrameEnum._1M }, open, null, Ct);
        Assert.True(cut.Value.HasMore);

        // A short range, nothing more.
        Assert.False((await gateway.GetCandlesAsync(EurUsdHourly, open, open.AddHours(2), Ct)).Value.HasMore);
    }

    [Fact]
    public async Task WhenCTraderHasMoreBarsThanOneAnswerHoldsAShorterRangeIsAskedNotTheNewestBarsPassedOn()
    {
        // cTrader answers the NEWEST bars when the range holds too many and says hasMore: the oldest part is the part that is missing.
        var from = Now.AddDays(-2);
        var to = Now.AddDays(-1);
        var connection = new ScriptedConnection
        {
            Trendbars = request => request.ToTimestamp - request.FromTimestamp > TimeSpan.FromHours(6).TotalMilliseconds
                ? new ProtoOAGetTrendbarsRes { HasMore = true, Trendbar = { Bar(to.AddMinutes(-1), 100_000, 0, 0, 0) } }
                : new ProtoOAGetTrendbarsRes { Trendbar = { Bar(from.AddHours(1), 100_000, 0, 0, 0) } },
        };

        var page = (await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly, from, to, Ct)).Value;

        // 24 hours, then 12, then 6: the first range that fits.
        Assert.Equal(3, connection.TrendbarRequests.Count);
        Assert.All(connection.TrendbarRequests, r => Assert.Equal(Ms(from), r.FromTimestamp));
        Assert.Equal([Ms(to), Ms(from.AddHours(12)), Ms(from.AddHours(6))], connection.TrendbarRequests.Select(r => r.ToTimestamp));
        // What comes back is the oldest part, and the worker is told to go on from after it.
        Assert.Equal(from.AddHours(1), Assert.Single(page.Items).TimeRange.OpenTime);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task ARangeThatNeverFitsIsAFailedCallNotAnEndlessLoop()
    {
        var connection = new ScriptedConnection { Trendbars = _ => new ProtoOAGetTrendbarsRes { HasMore = true, Trendbar = { Bar(Now.AddHours(-1), 100_000, 0, 0, 0) } } };

        var result = await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly, Now.AddDays(-1), Now.AddHours(-2), Ct);

        Assert.False(result.Success);
        Assert.Contains("more bars than one answer holds", result.Error);
        Assert.InRange(connection.TrendbarRequests.Count, 2, 20);
    }

    [Fact]
    public async Task BarsOutsideTheAskedRangeAreDroppedAndTheRestComesOldestFirst()
    {
        var from = new DateTime(2026, 11, 13, 2, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 11, 13, 4, 0, 0, DateTimeKind.Utc);
        var connection = new ScriptedConnection
        {
            Trendbars = _ => new ProtoOAGetTrendbarsRes
            {
                Trendbar =
                {
                    Bar(from.AddHours(3), 100_000, 0, 0, 0),    // after the end
                    Bar(from.AddHours(1), 100_000, 0, 0, 0),
                    Bar(from.AddHours(-1), 100_000, 0, 0, 0),   // before the start
                    Bar(from, 100_000, 0, 0, 0),
                    Bar(to, 100_000, 0, 0, 0),                  // exactly at the end: kept, the end is inclusive
                },
            },
        };

        var items = (await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly, from, to, Ct)).Value.Items;

        Assert.Equal([from, from.AddHours(1), to], items.Select(c => c.TimeRange.OpenTime));
    }

    [Fact]
    public async Task ASymbolTheBrokerDoesNotHaveIsAFailedCallNotAnException()
    {
        var connection = new ScriptedConnection();

        var result = await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly with { Symbol = new ExchangeSymbol("NOPE") }, Now.AddHours(-1), null, Ct);

        Assert.False(result.Success);
        Assert.Contains("NOPE", result.Error);
        Assert.Empty(connection.TrendbarRequests);
    }

    [Fact]
    public async Task WhenCTraderSaysNoTheCallFailsWithItsReasonAndNeverThrows()
    {
        var connection = new ScriptedConnection { Failure = new CtraderException("cTrader refused the request (REQUEST_FREQUENCY_EXCEEDED).", "REQUEST_FREQUENCY_EXCEEDED") };

        var result = await Build(connection).Gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-1), null, Ct);

        Assert.False(result.Success);
        Assert.Contains("REQUEST_FREQUENCY_EXCEEDED", result.Error);
    }

    [Fact]
    public async Task ASeriesOfAnotherExchangeOrAnUnsupportedTimeFrameIsRefusedBeforeAnythingIsAsked()
    {
        var connection = new ScriptedConnection();
        var gateway = Build(connection).Gateway;

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetCandlesAsync(EurUsdHourly with { Exchange = ExchangeEnum.Binance }, Now, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetCandlesAsync(EurUsdHourly with { MarketType = MarketTypeEnum.Futures }, Now, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.GetCandlesAsync(EurUsdHourly with { Timeframe = TimeFrameEnum._2H }, Now, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.GetCandlesAsync(EurUsdHourly with { Timeframe = TimeFrameEnum._1D }, Now, null, Ct));
        Assert.Empty(connection.TrendbarRequests);
        Assert.Equal(0, connection.SymbolListRequests);
    }

    [Fact]
    public void TheTimeFramesTheGatewayMapsAreExactlyTheOnesTheCollectorOffersForPepperstone()
    {
        Assert.Equal(
            MarketDataSupport.GetTimeFrames(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, MarketDataTypeEnum.Candle).Order(),
            CtraderTimeFrames.Supported.Order());
    }

    [Fact]
    public async Task TheSymbolListAndTheDetailsAreAskedForOnceAndAnyWritingOfTheNameFindsIt()
    {
        var connection = new ScriptedConnection();
        var (gateway, symbols, _) = Build(connection);

        foreach (var name in new[] { "EURUSD", "eurusd", "EUR/USD" })
            Assert.NotNull(await symbols.FindAsync(new ExchangeSymbol(name), Ct));
        await gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-2), null, Ct);
        await gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-2), null, Ct);

        Assert.Equal(1, connection.SymbolListRequests);
        Assert.Equal(1, connection.DetailRequests);
    }

    [Fact]
    public async Task ANameTheBrokerDoesNotHaveIsNotLookedForAgainForEveryTypoButIsAfterAWhile()
    {
        var connection = new ScriptedConnection();
        var (_, symbols, time) = Build(connection);

        Assert.Null(await symbols.FindAsync(new ExchangeSymbol("GBPUSD"), Ct));
        Assert.Equal(1, connection.SymbolListRequests);

        Assert.Null(await symbols.FindAsync(new ExchangeSymbol("GBPUSD"), Ct));
        Assert.Equal(1, connection.SymbolListRequests);

        // The broker lists it meanwhile.
        connection.Symbols.Add(new ProtoOALightSymbol { SymbolId = 2, SymbolName = "GBPUSD", Enabled = true });
        time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(2, (await symbols.FindAsync(new ExchangeSymbol("GBPUSD"), Ct))!.Id);
        Assert.Equal(2, connection.SymbolListRequests);
    }

    [Fact]
    public async Task TheListIsReadAgainAfterAnHour()
    {
        var connection = new ScriptedConnection();
        var (_, symbols, time) = Build(connection);
        await symbols.FindAsync(new ExchangeSymbol("EURUSD"), Ct);

        time.Advance(TimeSpan.FromMinutes(61));
        await symbols.FindAsync(new ExchangeSymbol("EURUSD"), Ct);

        Assert.Equal(2, connection.SymbolListRequests);
    }

    [Fact]
    public async Task TheCatalogFindsAnEnabledSymbolInTheBrokersSpellingAndNothingElse()
    {
        var connection = new ScriptedConnection();
        connection.Symbols.Add(new ProtoOALightSymbol { SymbolId = 3, SymbolName = "OLDPAIR", Enabled = false });
        var (_, symbols, _) = Build(connection);
        var catalog = new CtraderSymbolCatalog(symbols);

        var found = await catalog.FindTradingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("eur/usd"), Ct);
        var disabled = await catalog.FindTradingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("OLDPAIR"), Ct);
        var unknown = await catalog.FindTradingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("NOPE"), Ct);
        var wrongMarket = await catalog.FindTradingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Futures, new ExchangeSymbol("EURUSD"), Ct);

        Assert.Equal("EURUSD", found.Value!.Value.Value);
        Assert.Null(disabled.Value);
        Assert.Null(unknown.Value);
        Assert.True(wrongMarket.Success);
        Assert.Null(wrongMarket.Value);
    }

    [Fact]
    public async Task TheCatalogTellsAFailedCallFromAnAnswerOfNo()
    {
        var connection = new ScriptedConnection { Failure = new CtraderException("cTrader did not answer in time.") };
        var catalog = new CtraderSymbolCatalog(Build(connection).Symbols);

        var result = await catalog.FindTradingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("EURUSD"), Ct);

        Assert.False(result.Success);
        Assert.True(catalog.Serves(ExchangeEnum.Pepperstone));
        Assert.False(catalog.Serves(ExchangeEnum.Binance));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.FindTradingAsync(ExchangeEnum.Binance, MarketTypeEnum.Futures, new ExchangeSymbol("BTCUSDT"), Ct));
    }

    [Fact]
    public async Task TheSymbolsHoursGoToTheCalendarInTheBrokersTimeZone()
    {
        // The week of the broker: Sunday 17:00 to Friday 17:00 in New York.
        var connection = new ScriptedConnection
        {
            Details = new ProtoOASymbol
            {
                SymbolId = EurUsdId, Digits = 5, ScheduleTimeZone = "America/New_York",
                Schedule = { new ProtoOAInterval { StartSecond = 17 * 3600, EndSecond = (uint)(5 * 86400 + 17 * 3600) } },
            },
        };
        var calendar = new MarketCalendar();
        var (gateway, _, _) = Build(connection, calendar);
        await gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-2), null, Ct);

        // 2026-11-13 is a Friday and the clocks are on winter time (UTC-5): the market closed at 22:00 UTC and opens again on Sunday at 22:00 UTC.
        var weekend = new CandleGap(new DateTime(2026, 11, 13, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 15, 21, 0, 0, DateTimeKind.Utc));

        Assert.Empty(calendar.ExpectedPart(EurUsdHourly, weekend));
        Assert.Single(calendar.ExpectedPart(EurUsdHourly, weekend with { LastMissingOpenTime = weekend.LastMissingOpenTime.AddHours(1) }));
    }

    [Fact]
    public async Task HoursInATimeZoneThisMachineDoesNotKnowLeaveTheSymbolAlwaysOpen()
    {
        var connection = new ScriptedConnection
        {
            Details = new ProtoOASymbol
            {
                SymbolId = EurUsdId, Digits = 5, ScheduleTimeZone = "Mars/Olympus_Mons",
                Schedule = { new ProtoOAInterval { StartSecond = 0, EndSecond = 3600 } },
            },
        };
        var calendar = new MarketCalendar();
        var (gateway, _, _) = Build(connection, calendar);

        var result = await gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-2), null, Ct);

        Assert.True(result.Success);
        var gap = new CandleGap(Now.AddDays(-5), Now.AddDays(-4));
        Assert.Equal([gap], calendar.ExpectedPart(EurUsdHourly, gap));
    }

    [Fact]
    public async Task ASymbolWithNoScheduleIsAlwaysOpen()
    {
        var calendar = new MarketCalendar();
        var (gateway, _, _) = Build(new ScriptedConnection(), calendar);
        await gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-2), null, Ct);

        var gap = new CandleGap(Now.AddDays(-5), Now.AddDays(-4));
        Assert.Equal([gap], calendar.ExpectedPart(EurUsdHourly, gap));
    }

    private static ProtoOASymbol GoldDetails(params ProtoOAHoliday[] holidays)
    {
        // Sunday to Thursday 18:01 New York to the next day 16:59, Thursday to Friday 16:55.
        var details = new ProtoOASymbol { SymbolId = EurUsdId, Digits = 2, ScheduleTimeZone = "America/New_York", Holiday = { holidays } };
        for (var day = 0u; day < 5; day++)
            details.Schedule.Add(new ProtoOAInterval { StartSecond = day * 86400 + 18 * 3600 + 60, EndSecond = (day + 1) * 86400 + 16 * 3600 + (day == 4 ? 55u : 59u) * 60 });

        return details;
    }

    private static ProtoOAHoliday Holiday(string zone, DateTime date, int startSecond, int endSecond, bool recurring = false) => new()
    {
        HolidayId = 1, Name = "a holiday", ScheduleTimeZone = zone, IsRecurring = recurring,
        HolidayDate = (long)(date - DateTime.UnixEpoch).TotalDays, StartSecond = startSecond, EndSecond = endSecond,
    };

    [Fact]
    public async Task AnEarlyCloseTheBrokerListsInItsOwnTimeZoneShutsTheCalendarFromThen()
    {
        // As listed for gold on 2026-11-26 (Thanksgiving): 22:30 to 23:59 Moscow time, in the zone of the entry, not of the weekly hours.
        var connection = new ScriptedConnection { Details = GoldDetails(Holiday("Europe/Moscow", new DateTime(2026, 11, 26), 22 * 3600 + 30 * 60, 23 * 3600 + 59 * 60)) };
        var calendar = new MarketCalendar();
        var (gateway, _, _) = Build(connection, calendar);
        await gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-2), null, Ct);
        var series = EurUsdHourly with { Timeframe = TimeFrameEnum._1M };

        // 22:30 Moscow (UTC+3) is 19:30 UTC; the session of that day lasts to 21:59 UTC, and the rest of it is shut with the early close.
        var parts = calendar.ExpectedPart(series, new CandleGap(new DateTime(2026, 11, 26, 19, 25, 0, DateTimeKind.Utc), new DateTime(2026, 11, 26, 22, 30, 0, DateTimeKind.Utc)));

        Assert.Equal([new CandleGap(new DateTime(2026, 11, 26, 19, 25, 0, DateTimeKind.Utc), new DateTime(2026, 11, 26, 19, 29, 0, DateTimeKind.Utc))], parts);
    }

    [Fact]
    public async Task AHolidayWithoutHoursShutsTheWholeDay()
    {
        var connection = new ScriptedConnection { Details = GoldDetails(Holiday("Europe/Bucharest", new DateTime(2026, 12, 25), 0, 0)) };
        var calendar = new MarketCalendar();
        var (gateway, _, _) = Build(connection, calendar);
        await gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-2), null, Ct);

        // Friday 2026-12-25 in Bucharest (UTC+2): the whole Friday session is gone.
        var friday = new CandleGap(new DateTime(2026, 12, 24, 23, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 25, 21, 0, 0, DateTimeKind.Utc));

        Assert.Empty(calendar.ExpectedPart(EurUsdHourly, friday));
    }

    [Fact]
    public async Task AYearlyHolidayAndOneInAnUnknownTimeZoneAreLeftOutAndTheRestStillCounts()
    {
        var connection = new ScriptedConnection
        {
            Details = GoldDetails(
                Holiday("Europe/Moscow", new DateTime(2026, 11, 26), 0, 0, recurring: true),
                Holiday("Mars/Olympus_Mons", new DateTime(2026, 11, 26), 0, 0),
                Holiday("Europe/Bucharest", new DateTime(2026, 12, 25), 0, 0)),
        };
        var calendar = new MarketCalendar();
        var (gateway, _, _) = Build(connection, calendar);
        await gateway.GetCandlesAsync(EurUsdHourly, Now.AddHours(-2), null, Ct);

        // The two that cannot be used leave 2026-11-26 as it was (a Thursday, open); the third still shuts 2026-12-25.
        var thursday = new CandleGap(new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal([thursday], calendar.ExpectedPart(EurUsdHourly, thursday));
        Assert.Empty(calendar.ExpectedPart(EurUsdHourly,
            new CandleGap(new DateTime(2026, 12, 24, 23, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 25, 21, 0, 0, DateTimeKind.Utc))));
    }
}
