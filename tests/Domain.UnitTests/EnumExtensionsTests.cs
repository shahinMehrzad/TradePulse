using Domain.Enums;
using Domain.Extensions;

namespace Domain.UnitTests;

public class EnumExtensionsTests
{
    [Theory]
    [InlineData(TimeFrameEnum._4H, "4h")]
    [InlineData(TimeFrameEnum._1D, "1d")]
    public void TimeFramesLoseTheirUnderscore(TimeFrameEnum value, string expected) => Assert.Equal(expected, value.ToRouteName());

    [Fact]
    public void OtherEnumsAreJustLowercase()
    {
        Assert.Equal("binance", ExchangeEnum.Binance.ToRouteName());
        Assert.Equal("futures", MarketTypeEnum.Futures.ToRouteName());
    }

    [Fact]
    public void EveryMemberRoundTripsThroughItsRouteName()
    {
        foreach (var timeFrame in Enum.GetValues<TimeFrameEnum>())
        {
            Assert.True(EnumExtensions.TryParseRouteName<TimeFrameEnum>(timeFrame.ToRouteName(), out var parsed));
            Assert.Equal(timeFrame, parsed);
        }

        foreach (var market in Enum.GetValues<MarketTypeEnum>())
        {
            Assert.True(EnumExtensions.TryParseRouteName<MarketTypeEnum>(market.ToRouteName(), out var parsed));
            Assert.Equal(market, parsed);
        }
    }

    [Theory]
    [InlineData("Futures")]
    [InlineData("FUTURES")]
    [InlineData("futures")]
    public void ParsingIgnoresCase(string name)
    {
        Assert.True(EnumExtensions.TryParseRouteName<MarketTypeEnum>(name, out var parsed));
        Assert.Equal(MarketTypeEnum.Futures, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("options")]
    [InlineData("2")]              // a number, not a name
    [InlineData("60")]
    [InlineData("99")]
    [InlineData("spot,futures")]   // Enum.TryParse would combine these
    [InlineData("spot,spot")]
    public void OnlyNamesAreAccepted(string? value) => Assert.False(EnumExtensions.TryParseRouteName<MarketTypeEnum>(value, out _));

    [Theory]
    [InlineData("7h")]
    [InlineData("60")]
    [InlineData("1")]
    public void AnUnknownTimeFrameIsRefused(string value) => Assert.False(EnumExtensions.TryParseRouteName<TimeFrameEnum>(value, out _));
}
