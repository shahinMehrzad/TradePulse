using Application.Research.Watch;
using Domain.Entities;
using Domain.Enums;

namespace Application.UnitTests;

public class ShockAlertMessageTests
{
    private static ShockAlert Alert(decimal move = 0.0062m, int window = 30, decimal z = 26.4m) => new()
    {
        SymbolListingId = 1,
        TriggeredAt = new DateTime(2026, 10, 29, 20, 5, 47, DateTimeKind.Utc),
        Direction = move > 0 ? EventDirectionEnum.Up : EventDirectionEnum.Down,
        Level = AlertLevelEnum.Attention,
        WindowSeconds = window,
        Move = move,
        ZScore = move > 0 ? z : -z,
        StartPrice = 79893.4m,
        EndPrice = 79398.1m,
        Volume = 123.456m,
        FlaggedSeconds = 4,
        RuleVersion = "s30-z20-v2",
    };

    [Fact]
    public void ATitleSaysSymbolDirectionSizeAndWindow()
    {
        var up = ShockAlertMessage.Create(Alert(0.0062m), "BTCUSDT");
        var down = ShockAlertMessage.Create(Alert(-0.0202m, window: 900), "SUIUSDT");

        Assert.Equal("BTCUSDT +0.62 % in 30 s", up.Title);
        Assert.Equal("SUIUSDT -2.02 % in 15 min", down.Title);
    }

    [Theory]
    [InlineData(30, "30 s")]
    [InlineData(90, "90 s")]
    [InlineData(120, "2 min")]
    [InlineData(300, "5 min")]
    [InlineData(900, "15 min")]
    [InlineData(3600, "60 min")]
    public void TheWindowIsInSecondsBelowTwoMinutesAndInMinutesAbove(int seconds, string expected)
    {
        Assert.Equal(expected, ShockAlertMessage.Window(seconds));
    }

    [Fact]
    public void TheBodyGivesPricesHowManyTimesUsualAndWhen()
    {
        var body = ShockAlertMessage.Create(Alert(), "BTCUSDT").Body;

        Assert.Equal("79893.4 -> 79398.1, 26.4x its usual move, 20:05 UTC", body);
    }

    [Fact]
    public void TheTelegramTextHasTheSymbolInBoldAndAnEmojiForTheDirection()
    {
        var up = ShockAlertMessage.Create(Alert(0.0062m), "BTCUSDT").Html;
        var down = ShockAlertMessage.Create(Alert(-0.0202m, window: 900, z: 5.3m), "SUIUSDT").Html;

        Assert.Equal("🟢⬆️ <b>BTCUSDT</b> +0.62 % in 30 s\n79893.4 → 79398.1 · 26.4x usual · 20:05 UTC", up);
        Assert.StartsWith("🔴⬇️ <b>SUIUSDT</b> -2.02 % in 15 min", down);
        Assert.Contains("5.3x usual", down);
    }

    [Fact]
    public void ThePlainTextHasNoFormattingAndNoEmoji()
    {
        var message = ShockAlertMessage.Create(Alert(), "BTCUSDT");

        Assert.DoesNotContain("<", message.Title + message.Body);
        Assert.All(message.Title + message.Body, c => Assert.True(c < 0x2000, "plain text for the console and the mail"));
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
    public void ACheapCoinShowsThePricesThatMakeTheMove()
    {
        var alert = Alert(0.0044m);
        alert.StartPrice = 0.9061m;
        alert.EndPrice = 0.9101m;

        var message = ShockAlertMessage.Create(alert, "SUIUSDT");

        Assert.Contains("0.9061 -> 0.9101", message.Body);
        Assert.Contains("0.9061 → 0.9101", message.Html);
    }

    [Fact]
    public void TheWordsDoNotAdviseOrPredict()
    {
        var message = ShockAlertMessage.Create(Alert(), "BTCUSDT");
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
            var message = ShockAlertMessage.Create(Alert(), "BTCUSDT");

            Assert.Equal("BTCUSDT +0.62 % in 30 s", message.Title);
            Assert.Contains("79893.4", message.Body);
            Assert.Contains("26.4x", message.Html);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Fact]
    public void AnythingThatCouldBeTakenForMarkupIsEscapedInTheTelegramText()
    {
        var message = ShockAlertMessage.Create(Alert(), "A<B>&C");

        Assert.Contains("<b>A&lt;B&gt;&amp;C</b>", message.Html);
    }

    [Fact]
    public void TheWarmUpMessageSaysWhichSymbolHowLongAndWhatToDoAboutIt()
    {
        var message = ShockAlertMessage.WarmingUp("ETHUSDT", TimeSpan.FromSeconds(3600));

        Assert.Equal("ETHUSDT warming up", message.Title);
        Assert.Contains("60 min", message.Body);
        Assert.Contains("Collect them", message.Body);
        Assert.StartsWith("⏳ <b>ETHUSDT</b> warming up", message.Html);
    }

    [Fact]
    public void AMessageAboutTheWatchItselfSaysWhatWasNotSeen()
    {
        var blind = ShockAlertMessage.Blind("BTCUSDT, ETHUSDT", TimeSpan.FromSeconds(20));
        var restored = ShockAlertMessage.Restored("BTCUSDT, ETHUSDT", TimeSpan.FromSeconds(95));

        Assert.Equal("Watch is blind", blind.Title);
        Assert.Contains("BTCUSDT, ETHUSDT", blind.Body);
        Assert.Contains("20 s", blind.Body);
        Assert.StartsWith("⚠️ <b>Watch is blind</b>", blind.Html);
        Assert.Equal("Watch sees the market again", restored.Title);
        Assert.Contains("95 s", restored.Body);
        Assert.StartsWith("✅ <b>Watch sees the market again</b>", restored.Html);
    }

    [Fact]
    public void TheStartMessageSaysWhatIsFollowedAndTheTwoChecks()
    {
        var message = ShockAlertMessage.Started("the symbols switched on in the admin panel", new ShockWatchOptions());

        Assert.Equal("Watch started", message.Title);
        Assert.Contains("the symbols switched on in the admin panel", message.Body);
        Assert.Contains("Sudden: 30 s at 20x", message.Body);
        Assert.Contains("Gradual: 15 min at 4.5x", message.Body);
        Assert.Contains("s30-z20-v2", message.Body);
        Assert.Contains("m15-z4.5-v2", message.Body);
        Assert.StartsWith("👀 <b>Watch started</b>", message.Html);
    }
}
