using Application.Research.Watch;
using Domain.Entities;
using Domain.Enums;

namespace Application.UnitTests;

public class ShockAlertMessageTests
{
    private static ShockAlert Alert(decimal move = 0.0062m, AlertLevelEnum level = AlertLevelEnum.Alarm) => new()
    {
        SymbolListingId = 1,
        TriggeredAt = new DateTime(2026, 10, 29, 20, 34, 15, DateTimeKind.Utc),
        Direction = move > 0 ? EventDirectionEnum.Up : EventDirectionEnum.Down,
        Level = level,
        WindowSeconds = 30,
        Move = move,
        ZScore = move > 0 ? 6.8m : -6.8m,
        StartPrice = 79893.4m,
        EndPrice = 79398.1m,
        Volume = 123.456m,
        FlaggedSeconds = 4,
        RuleVersion = "s30-0.003-0.005-z6-v1",
    };

    [Fact]
    public void ATitleSaysLevelSymbolDirectionAndSize()
    {
        var up = ShockAlertMessage.Create(Alert(0.0062m), "BTCUSDT", ShockSignalKind.Started);
        var down = ShockAlertMessage.Create(Alert(-0.0031m, AlertLevelEnum.Attention), "BTCUSDT", ShockSignalKind.Started);

        Assert.Equal("ALARM: BTCUSDT rose 0.62 % in 30 s", up.Title);
        Assert.Equal("Attention: BTCUSDT fell 0.31 % in 30 s", down.Title);
    }

    [Fact]
    public void TheTitleSaysWhenTheAlertWasRaisedOrIsOver()
    {
        Assert.EndsWith("(raised)", ShockAlertMessage.Create(Alert(), "BTCUSDT", ShockSignalKind.Escalated).Title);
        Assert.EndsWith("(over)", ShockAlertMessage.Create(Alert(), "BTCUSDT", ShockSignalKind.Closed).Title);
    }

    [Fact]
    public void TheBodyGivesTimePricesZScoreVolumeAndRule()
    {
        var body = ShockAlertMessage.Create(Alert(), "BTCUSDT", ShockSignalKind.Started).Body;

        Assert.Contains("Since: 2026-10-29 20:34:15 UTC", body);
        Assert.Contains("Price: 79893.4 -> 79398.1", body);
        Assert.Contains("Unusual by: 6.8 standard deviations", body);
        Assert.Contains("Traded in the window: 123.456 BTC", body);
        Assert.Contains("Flagged seconds: 4", body);
        Assert.Contains("Rule: s30-0.003-0.005-z6-v1", body);
    }

    [Theory]
    [InlineData(79893.4, "79893.4")]
    [InlineData(0.906, "0.906")]
    [InlineData(0.9, "0.9")]
    [InlineData(0.91, "0.91")]
    [InlineData(1.2345678, "1.23457")]
    [InlineData(0.00001234567, "0.00001235")]
    [InlineData(2500, "2500")]
    [InlineData(123456.789, "123457")]
    [InlineData(0, "0")]
    public void APriceKeepsSixSignificantDigitsSoACheapCoinIsNotRoundedAwayAndNothingIsPadded(double price, string expected)
    {
        Assert.Equal(expected, ShockAlertMessage.Price((decimal)price));
    }

    [Fact]
    public void ACheapCoinShowsThePricesThatMakeTheMoveAndTheVolumeInItsOwnCoin()
    {
        var alert = Alert(0.0044m, AlertLevelEnum.Attention);
        alert.StartPrice = 0.9061m;
        alert.EndPrice = 0.9101m;
        alert.Volume = 82607.9m;

        var body = ShockAlertMessage.Create(alert, "SUIUSDT", ShockSignalKind.Started).Body;

        Assert.Contains("Price: 0.9061 -> 0.9101", body);
        Assert.Contains("Traded in the window: 82607.9 SUI", body);
    }

    [Theory]
    [InlineData("ETHUSDT", " ETH")]
    [InlineData("BTCUSDC", " BTC")]
    [InlineData("XAUUSDT", " XAU")]
    [InlineData("SOLUSD", " SOL")]
    [InlineData("1000PEPEUSDT", " 1000PEPE")]
    [InlineData("USDT", "")]       // nothing is left of it
    [InlineData("BTCEUR", "")]     // an unknown quote asset gets no unit rather than a wrong one
    public void TheVolumeNamesTheBaseAssetOnlyWhenTheQuoteAssetIsKnown(string symbol, string unit)
    {
        var body = ShockAlertMessage.Create(Alert(), symbol, ShockSignalKind.Started).Body;

        Assert.Contains("Traded in the window: 123.456" + unit + Environment.NewLine, body);
    }

    [Fact]
    public void TheWordsDoNotAdviseOrPredict()
    {
        var message = ShockAlertMessage.Create(Alert(), "BTCUSDT", ShockSignalKind.Started);
        var text = (message.Title + " " + message.Body).ToLowerInvariant();

        Assert.DoesNotContain("buy", text);
        Assert.DoesNotContain("sell", text);
        Assert.DoesNotContain("will", text);
        Assert.DoesNotContain("expect", text);
    }

    [Fact]
    public void TheNumbersDoNotDependOnTheMachinesCulture()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");
            var message = ShockAlertMessage.Create(Alert(), "BTCUSDT", ShockSignalKind.Started);

            Assert.Equal("ALARM: BTCUSDT rose 0.62 % in 30 s", message.Title);
            Assert.Contains("79893.4", message.Body);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Fact]
    public void TheWarmUpMessageSaysWhichSymbolHowLongAndWhatToDoAboutIt()
    {
        var message = ShockAlertMessage.WarmingUp("ETHUSDT", TimeSpan.FromSeconds(3600));

        Assert.Equal("Watch is warming up: ETHUSDT", message.Title);
        Assert.Contains("ETHUSDT", message.Body);
        Assert.Contains("60 minutes", message.Body);
        Assert.Contains("Collect its 1 minute candles", message.Body);
    }

    [Fact]
    public void AMessageAboutTheWatchItselfSaysWhatWasNotSeen()
    {
        var blind = ShockAlertMessage.Blind("BTCUSDT, ETHUSDT", TimeSpan.FromSeconds(20));
        var restored = ShockAlertMessage.Restored("BTCUSDT, ETHUSDT", TimeSpan.FromSeconds(95));

        Assert.Equal("Watch is blind", blind.Title);
        Assert.Contains("BTCUSDT, ETHUSDT", blind.Body);
        Assert.Contains("20 s", blind.Body);
        Assert.Equal("Watch sees the market again", restored.Title);
        Assert.Contains("95 s", restored.Body);
    }
}
