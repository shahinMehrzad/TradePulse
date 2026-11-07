using Domain.Enums;
using Domain.Services;

namespace Domain.UnitTests;

public class PaperSignalIdTests
{
    [Fact]
    public void Build_MatchesTheContractExample() =>
        Assert.Equal("S1:BTCUSDT:4h:2026-11-07T04:00Z:long",
            PaperSignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Long));

    [Fact]
    public void Build_UsesTheCandleOpenTimeNotTheCloseTime() =>
        Assert.Contains("T04:00Z", PaperSignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Short));

    [Fact]
    public void Build_WritesTheSideInLowerCase() =>
        Assert.EndsWith(":short", PaperSignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Short));

    [Fact]
    public void Build_DropsTheSlashAndUpperCasesTheSymbol() =>
        Assert.StartsWith("S1:ADAUSDT:", PaperSignalId.Build("S1", "ada/usdt", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Long));

    [Theory]
    [InlineData(TimeFrameEnum._15M, "15m")]
    [InlineData(TimeFrameEnum._1H, "1h")]
    [InlineData(TimeFrameEnum._1D, "1d")]
    public void Build_NamesTheTimeFrameLikeTheUrl(TimeFrameEnum timeFrame, string expected) =>
        Assert.Contains($":{expected}:", PaperSignalId.Build("S1", "BTC/USDT", timeFrame, PaperTestData.CandleOpen, PositionSideEnum.Long));

    [Fact]
    public void Build_TreatsAnUnspecifiedKindAsUtc()
    {
        var unspecified = DateTime.SpecifyKind(PaperTestData.CandleOpen, DateTimeKind.Unspecified);

        Assert.Equal(
            PaperSignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Long),
            PaperSignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, unspecified, PositionSideEnum.Long));
    }

    [Fact]
    public void Build_GivesTheLongAndTheShortOfTheSameCandleDifferentIds() =>
        Assert.NotEqual(
            PaperSignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Long),
            PaperSignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Short));
}
