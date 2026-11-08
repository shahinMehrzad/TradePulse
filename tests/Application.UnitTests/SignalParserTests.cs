using System.Text.Json;
using Application.Signals;
using Domain.Enums;
using Domain.ValueObjects;
using static Application.UnitTests.SignalTestData;

namespace Application.UnitTests;

/// <summary>What the body of a signal must say, and nothing is guessed when it does not.</summary>
public class SignalParserTests
{
    /// <summary>The body a sender makes for a signal, with a change if a test wants one.</summary>
    internal static string Json(TradeSignal signal, Action<Dictionary<string, object?>>? change = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["signalId"] = signal.SignalId,
            ["strategy"] = signal.Strategy,
            ["exchange"] = signal.Exchange.ToString(),
            ["market"] = signal.Market.ToString(),
            ["symbol"] = signal.Symbol,
            ["side"] = signal.Side.ToString(),
            ["leverage"] = signal.Leverage,
            ["takeProfit"] = signal.TakeProfit,
            ["stopLoss"] = signal.StopLoss,
            ["signalPrice"] = signal.SignalPrice,
            ["timeframe"] = "4h",
            ["candleOpenTime"] = signal.CandleOpenTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["candleCloseTime"] = signal.CandleCloseTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["validUntil"] = signal.ValidUntil.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["maxHours"] = signal.MaxDuration?.TotalHours,
        };
        change?.Invoke(fields);
        return JsonSerializer.Serialize(fields);
    }

    private static string Error(string body)
    {
        Assert.False(SignalParser.TryParse(body, out var signal, out var error));
        Assert.Null(signal);
        return error!;
    }

    [Fact]
    public void AValidBodyIsTheSignalItWasMadeFrom()
    {
        var sent = Signal(PositionSideEnum.Short, maxDuration: TimeSpan.FromHours(48));

        Assert.True(SignalParser.TryParse(Json(sent), out var signal, out var error));

        Assert.Null(error);
        Assert.Equal(sent, signal);
    }

    [Fact]
    public void TheExchangeIsBinanceWhenTheSenderDoesNotSay()
    {
        foreach (var body in new[] { Json(Signal(), f => f.Remove("exchange")), Json(Signal(), f => f["exchange"] = null) })
        {
            Assert.True(SignalParser.TryParse(body, out var signal, out _));
            Assert.Equal(ExchangeEnum.Binance, signal.Exchange);
        }
    }

    [Fact]
    public void TheNamesOfTheFieldsAreNotCaseSensitive()
    {
        var body = Json(Signal()).Replace("\"signalId\"", "\"SignalID\"").Replace("\"takeProfit\"", "\"TAKEPROFIT\"");

        Assert.True(SignalParser.TryParse(body, out var signal, out _));
        Assert.Equal(104m, signal.TakeProfit);
    }

    [Fact]
    public void FieldsTheContractDoesNotKnowAreIgnored()
    {
        Assert.True(SignalParser.TryParse(Json(Signal(), f => f["comment"] = "hello"), out _, out _));
    }

    [Fact]
    public void AnythingWithoutMaxHoursHasNoDuration()
    {
        Assert.True(SignalParser.TryParse(Json(Signal(), f => f.Remove("maxHours")), out var absent, out _));
        Assert.True(SignalParser.TryParse(Json(Signal(), f => f["maxHours"] = null), out var empty, out _));

        Assert.Null(absent.MaxDuration);
        Assert.Null(empty.MaxDuration);
    }

    [Fact]
    public void MaxHoursIsADurationInHours()
    {
        Assert.True(SignalParser.TryParse(Json(Signal(), f => f["maxHours"] = 36), out var signal, out _));

        Assert.Equal(TimeSpan.FromHours(36), signal.MaxDuration);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"48\"")]
    [InlineData("100000")]
    public void MaxHoursThatIsNotAPositiveNumberOfHoursIsRefused(string value) =>
        Assert.Contains("maxHours", Error(Json(Signal()).Replace("\"maxHours\":null", $"\"maxHours\":{value}")));

    [Fact]
    public void ATimeWithAnOffsetBecomesUtc()
    {
        var body = Json(Signal(), f => f["candleOpenTime"] = "2026-11-07T07:00:00+03:00");

        Assert.True(SignalParser.TryParse(body, out var signal, out _));
        Assert.Equal(new DateTime(2026, 11, 7, 4, 0, 0, DateTimeKind.Utc), signal.CandleOpenTime);
        Assert.Equal(DateTimeKind.Utc, signal.CandleOpenTime.Kind);
    }

    [Theory]
    [InlineData("2026-11-07T04:00:00")]
    [InlineData("2026-11-07 04:00:00")]
    [InlineData("yesterday")]
    [InlineData("")]
    public void ATimeWithoutAZoneOrNotATimeIsRefused(string time) =>
        Assert.Contains("candleOpenTime", Error(Json(Signal(), f => f["candleOpenTime"] = time)));

    [Theory]
    [InlineData("signalId")]
    [InlineData("strategy")]
    [InlineData("market")]
    [InlineData("symbol")]
    [InlineData("side")]
    [InlineData("leverage")]
    [InlineData("takeProfit")]
    [InlineData("stopLoss")]
    [InlineData("signalPrice")]
    [InlineData("timeframe")]
    [InlineData("candleOpenTime")]
    [InlineData("candleCloseTime")]
    [InlineData("validUntil")]
    public void EveryRequiredFieldIsNamedWhenItIsMissing(string field) =>
        Assert.Contains(field, Error(Json(Signal(), f => f.Remove(field))), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("leverage")]
    [InlineData("takeProfit")]
    [InlineData("stopLoss")]
    [InlineData("signalPrice")]
    public void ANumberMustBeANumberAboveZero(string field)
    {
        Assert.Contains(field, Error(Json(Signal(), f => f[field] = 0)));
        Assert.Contains(field, Error(Json(Signal(), f => f[field] = -1)));
        Assert.Contains(field, Error(Json(Signal(), f => f[field] = "5")));
        Assert.Contains(field, Error(Json(Signal(), f => f[field] = null)));
    }

    [Theory]
    [InlineData("side", "1")]
    [InlineData("side", "Sideways")]
    [InlineData("market", "2")]
    [InlineData("market", "Options")]
    [InlineData("exchange", "Nowhere")]
    [InlineData("exchange", "7")]
    public void AnEnumIsGivenByItsNameNeverByItsNumber(string field, string value) =>
        Assert.Contains(field, Error(Json(Signal(), f => f[field] = value)));

    [Theory]
    [InlineData("long")]
    [InlineData("LONG")]
    public void TheNameOfASideMayBeInAnyCase(string side)
    {
        Assert.True(SignalParser.TryParse(Json(Signal(), f => f["side"] = side), out var signal, out _));
        Assert.Equal(PositionSideEnum.Long, signal.Side);
    }

    [Theory]
    [InlineData("4h", TimeFrameEnum._4H)]
    [InlineData("15m", TimeFrameEnum._15M)]
    [InlineData("1d", TimeFrameEnum._1D)]
    public void ATimeFrameIsGivenLikeTheUrlOfTheApi(string text, TimeFrameEnum expected)
    {
        Assert.True(SignalParser.TryParse(Json(Signal(), f => f["timeframe"] = text), out var signal, out _));
        Assert.Equal(expected, signal.TimeFrame);
    }

    [Theory]
    [InlineData("7x")]
    [InlineData("14400")]
    [InlineData("4h,1h")]
    public void ATimeFrameThatIsNotKnownIsRefused(string text) =>
        Assert.Contains("timeframe", Error(Json(Signal(), f => f["timeframe"] = text)));

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData("btc/usdt")]
    [InlineData("BTC/")]
    [InlineData("/USDT")]
    [InlineData("BTC/USDT/X")]
    [InlineData("BTC USDT")]
    [InlineData("B/U")]
    public void ASymbolMustLookLikeBaseSlashQuote(string symbol) =>
        Assert.Contains("symbol", Error(Json(Signal(), f => f["symbol"] = symbol)));

    [Theory]
    [InlineData("a:b")]
    [InlineData("has space")]
    [InlineData("")]
    public void AStrategyMustBeAValidLabel(string strategy) =>
        Assert.Contains("strategy", Error(Json(Signal(), f => f["strategy"] = strategy)));

    [Fact]
    public void ASignalIdThatIsTooLongIsRefused() =>
        Assert.Contains("signalId", Error(Json(Signal(), f => f["signalId"] = new string('x', SignalParser.MaxSignalIdLength + 1))));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":1}}}}}")]
    public void ABodyThatIsNotAJsonObjectOfTheRightDepthIsRefused(string body)
    {
        Assert.False(SignalParser.TryParse(body, out var signal, out var error));
        Assert.Null(signal);
        Assert.NotNull(error);
    }

    [Fact]
    public void AnErrorNeverRepeatsWhatWasSent()
    {
        var error = Error(Json(Signal(), f => f["symbol"] = "SECRET-LOOKING-VALUE"));

        Assert.DoesNotContain("SECRET-LOOKING-VALUE", error);
    }
}
