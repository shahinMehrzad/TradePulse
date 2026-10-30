using System.Globalization;
using Domain.Entities;
using Domain.Enums;
using Domain.Extensions;

namespace Application.Commands.AdminConfigurations;

/// <summary>The words the audit log uses for a setting: which one it is, and what is different between two versions of it.</summary>
internal static class AuditDescriptions
{
    public static string Summary(CandleWorkerConfiguration c) =>
        $"{Name((ExchangeEnum)c.ExchangeId)} / {Name((MarketTypeEnum)c.MarketId)} / {Name((SymbolEnum)c.SymbolId)} / {Name((TimeFrameEnum)c.TimeFrameId)}";

    public static string Summary(SymbolDataConfiguration c) =>
        $"{Name((ExchangeEnum)c.ExchangeId)} / {Name((MarketTypeEnum)c.MarketId)} / {Name((SymbolEnum)c.SymbolId)}";

    /// <summary>The values of a series that was just added (or removed).</summary>
    public static string Values(CandleWorkerConfiguration c) =>
        $"start date {Date(c.StartDate)}, open interest {OnOff(c.CollectOpenInterest)}, market statistics {OnOff(c.CollectMarketStatistics)}";

    public static string Values(SymbolDataConfiguration c) =>
        $"funding rate {OnOff(c.CollectFundingRate)}{(c.FundingStartDate is { } start ? $" from {Date(start)}" : "")}, liquidations {OnOff(c.CollectLiquidation)}";

    /// <summary>What an update changed, or null when it changed nothing.</summary>
    public static string? Changes(CandleWorkerConfiguration before, CandleWorkerConfiguration after) => Join(
        Change("exchange", Name((ExchangeEnum)before.ExchangeId), Name((ExchangeEnum)after.ExchangeId)),
        Change("market", Name((MarketTypeEnum)before.MarketId), Name((MarketTypeEnum)after.MarketId)),
        Change("symbol", Name((SymbolEnum)before.SymbolId), Name((SymbolEnum)after.SymbolId)),
        Change("time frame", Name((TimeFrameEnum)before.TimeFrameId), Name((TimeFrameEnum)after.TimeFrameId)),
        Change("start date", Date(before.StartDate), Date(after.StartDate)),
        Change("open interest", OnOff(before.CollectOpenInterest), OnOff(after.CollectOpenInterest)),
        Change("market statistics", OnOff(before.CollectMarketStatistics), OnOff(after.CollectMarketStatistics)));

    public static string? Changes(SymbolDataConfiguration before, SymbolDataConfiguration after) => Join(
        Change("exchange", Name((ExchangeEnum)before.ExchangeId), Name((ExchangeEnum)after.ExchangeId)),
        Change("market", Name((MarketTypeEnum)before.MarketId), Name((MarketTypeEnum)after.MarketId)),
        Change("symbol", Name((SymbolEnum)before.SymbolId), Name((SymbolEnum)after.SymbolId)),
        Change("funding rate", OnOff(before.CollectFundingRate), OnOff(after.CollectFundingRate)),
        Change("funding start date", Date(before.FundingStartDate), Date(after.FundingStartDate)),
        Change("liquidations", OnOff(before.CollectLiquidation), OnOff(after.CollectLiquidation)));

    private static string Name<T>(T value) where T : struct, Enum => value.ToRouteName();

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Date(DateTime? value) => value is { } v ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "none";

    private static string? Change(string what, string before, string after) => before == after ? null : $"{what} {before} -> {after}";

    private static string? Join(params string?[] changes) =>
        string.Join("; ", changes.Where(c => c is not null)) is { Length: > 0 } joined ? joined : null;
}
