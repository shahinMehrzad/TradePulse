using Domain.Enums;

namespace Domain.UnitTests;

/// <summary>
/// Enum members are shared between services, so their numeric values must never be renumbered or reused.
/// Adding a member is fine: add it to the list below. Changing or removing one fails here on purpose.
/// Symbols are not here: they are rows of the database now (the first fourteen are pinned by the config database tests).
/// </summary>
public class EnumContractTests
{
    [Fact]
    public void ExchangeValuesAreStable() =>
        AssertContract<ExchangeEnum>(("Binance", 1));

    [Fact]
    public void MarketTypeValuesAreStable() =>
        AssertContract<MarketTypeEnum>(("Spot", 1), ("Futures", 2));

    [Fact]
    public void OrderSideValuesAreStable() =>
        AssertContract<OrderSideEnum>(("Buy", 1), ("Sell", 2));

    private static void AssertContract<TEnum>(params (string Name, int Value)[] expected) where TEnum : struct, Enum
    {
        var actual = Enum.GetValues<TEnum>()
            .Select(v => (Name: v.ToString(), Value: Convert.ToInt32(v)))
            .OrderBy(x => x.Value)
            .ToArray();

        Assert.Equal(expected.OrderBy(x => x.Value).ToArray(), actual);
    }
}
