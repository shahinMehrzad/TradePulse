using Domain.Enums;

namespace Domain.Entities
{
    /// <summary>
    /// A forced liquidation order. <see cref="OrderSideEnum.Sell"/> closes a long position,
    /// <see cref="OrderSideEnum.Buy"/> closes a short one.
    /// </summary>
    public readonly record struct Liquidation(
        DateTime Time,
        OrderSideEnum Side,
        string OrderType,
        string TimeInForce,
        string Status,
        decimal Quantity,
        decimal Price,
        decimal AveragePrice,
        decimal LastFilledQuantity,
        decimal AccumulatedQuantity);
}
