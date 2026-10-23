using BackgroundWorker.SymbolData;
using Domain.Enums;
using Domain.ValueObjects;

namespace BackgroundWorker.UnitTests.SymbolData;

public class SymbolDataPlansTests
{
    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly SymbolDataPlan Both = SymbolDataTestSupport.Binance(SymbolEnum.BTCUSDT, funding: true, liquidation: true);
    private static readonly SymbolDataPlan FundingOnly = SymbolDataTestSupport.Binance(SymbolEnum.ETHUSDT, funding: true, liquidation: false);
    private static readonly SymbolDataPlan LiquidationOnly = SymbolDataTestSupport.Binance(SymbolEnum.BNBUSDT, funding: false, liquidation: true);

    [Fact]
    public void FundingRatesAreTakenFromTheSymbolsThatAskedForThemWithTheirStartDate()
    {
        var targets = SymbolDataPlans.FundingRatesForBinance([Both, FundingOnly, LiquidationOnly]);

        Assert.Equal(
            [new FundingRateTarget(SymbolEnum.BTCUSDT, Start), new FundingRateTarget(SymbolEnum.ETHUSDT, Start)],
            targets);
    }

    [Fact]
    public void LiquidationsAreTakenFromTheSymbolsThatAskedForThem()
    {
        Assert.Equal([SymbolEnum.BTCUSDT, SymbolEnum.BNBUSDT], SymbolDataPlans.LiquidationsForBinance([Both, FundingOnly, LiquidationOnly]));
    }

    [Fact]
    public void NothingAskedForMeansNothingSelectedAndNothingReported()
    {
        var refused = new List<string>();
        SymbolDataPlan[] plans = [SymbolDataTestSupport.Binance(funding: false, liquidation: false)];

        Assert.Empty(SymbolDataPlans.FundingRatesForBinance(plans, (_, why) => refused.Add(why)));
        Assert.Empty(SymbolDataPlans.LiquidationsForBinance(plans, (_, why) => refused.Add(why)));
        Assert.Empty(refused);
    }

    [Fact]
    public void AnotherExchangesPlansAreNotThisWorkersBusiness()
    {
        SymbolDataPlan[] plans = [Both with { Exchange = (ExchangeEnum)99 }];

        Assert.Empty(SymbolDataPlans.FundingRatesForBinance(plans));
        Assert.Empty(SymbolDataPlans.LiquidationsForBinance(plans));
    }

    [Fact]
    public void ChoicesTheMarketCannotHonourAreReportedAndLeftOutWithoutStoppingTheOthers()
    {
        var onSpot = SymbolDataTestSupport.Binance(SymbolEnum.XRPUSDT, market: MarketTypeEnum.Spot);
        var refusedFunding = new List<(SymbolDataPlan Plan, string Reason)>();
        var refusedLiquidation = new List<(SymbolDataPlan Plan, string Reason)>();

        var funding = SymbolDataPlans.FundingRatesForBinance([onSpot, Both], (plan, why) => refusedFunding.Add((plan, why)));
        var liquidation = SymbolDataPlans.LiquidationsForBinance([onSpot, Both], (plan, why) => refusedLiquidation.Add((plan, why)));

        Assert.Equal(SymbolEnum.BTCUSDT, Assert.Single(funding).Symbol);
        Assert.Equal([SymbolEnum.BTCUSDT], liquidation);
        Assert.Equal((onSpot, "FundingRate is not available on Binance Spot"), Assert.Single(refusedFunding));
        Assert.Equal((onSpot, "Liquidation is not available on Binance Spot"), Assert.Single(refusedLiquidation));
    }

    [Fact]
    public void AFundingRateWithoutADateIsReportedAndLeftOut()
    {
        var noDate = Both with { FundingStartDate = null };
        var refused = new List<string>();

        var targets = SymbolDataPlans.FundingRatesForBinance([noDate], (_, why) => refused.Add(why));

        Assert.Empty(targets);
        Assert.Equal(["the funding rate has no start date"], refused);
    }

    [Fact]
    public void ASymbolListedTwiceIsCollectedOnce()
    {
        Assert.Single(SymbolDataPlans.FundingRatesForBinance([Both, Both]));
        Assert.Single(SymbolDataPlans.LiquidationsForBinance([Both, Both]));
    }
}
