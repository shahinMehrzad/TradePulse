using System.Globalization;
using Domain.Entities;
using Domain.Enums;
using Domain.Extensions;

namespace Application.Commands.AdminConfigurations;

/// <summary>The words the audit log uses for a setting: which one it is, and what is different between two versions of it.</summary>
internal static class AuditDescriptions
{
    public static string Summary(CandleWorkerConfiguration c) =>
        $"{Where(c.SymbolListing)} / {Name((TimeFrameEnum)c.TimeFrameId)}";

    public static string Summary(SymbolDataConfiguration c) => Where(c.SymbolListing);

    /// <summary>The values of a series that was just added (or removed).</summary>
    public static string Values(CandleWorkerConfiguration c) =>
        $"start date {Date(c.StartDate)}, open interest {OnOff(c.CollectOpenInterest)}, market statistics {OnOff(c.CollectMarketStatistics)}";

    public static string Values(SymbolDataConfiguration c) =>
        $"funding rate {OnOff(c.CollectFundingRate)}{(c.FundingStartDate is { } start ? $" from {Date(start)}" : "")}, liquidations {OnOff(c.CollectLiquidation)}";

    /// <summary>What an update changed, or null when it changed nothing.</summary>
    public static string? Changes(CandleWorkerConfiguration before, CandleWorkerConfiguration after) => Join(
        Change("exchange", Exchange(before.SymbolListing), Exchange(after.SymbolListing)),
        Change("market", Market(before.SymbolListing), Market(after.SymbolListing)),
        Change("symbol", Symbol(before.SymbolListing), Symbol(after.SymbolListing)),
        Change("time frame", Name((TimeFrameEnum)before.TimeFrameId), Name((TimeFrameEnum)after.TimeFrameId)),
        Change("start date", Date(before.StartDate), Date(after.StartDate)),
        Change("open interest", OnOff(before.CollectOpenInterest), OnOff(after.CollectOpenInterest)),
        Change("market statistics", OnOff(before.CollectMarketStatistics), OnOff(after.CollectMarketStatistics)));

    public static string? Changes(SymbolDataConfiguration before, SymbolDataConfiguration after) => Join(
        Change("exchange", Exchange(before.SymbolListing), Exchange(after.SymbolListing)),
        Change("market", Market(before.SymbolListing), Market(after.SymbolListing)),
        Change("symbol", Symbol(before.SymbolListing), Symbol(after.SymbolListing)),
        Change("funding rate", OnOff(before.CollectFundingRate), OnOff(after.CollectFundingRate)),
        Change("funding start date", Date(before.FundingStartDate), Date(after.FundingStartDate)),
        Change("liquidations", OnOff(before.CollectLiquidation), OnOff(after.CollectLiquidation)));

    private static string Where(SymbolListing? l) => $"{Exchange(l)} / {Market(l)} / {Symbol(l)}";

    // The listing is loaded with every row the repositories hand out; "?" only shows a row that was built by hand.
    private static string Exchange(SymbolListing? l) => l is null ? "?" : Name((ExchangeEnum)l.ExchangeId);

    private static string Market(SymbolListing? l) => l is null ? "?" : Name((MarketTypeEnum)l.MarketId);

    private static string Symbol(SymbolListing? l) => l is null ? "?" : l.ExchangeSymbol.ToLowerInvariant();

    private static string Name<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToRouteName() : $"#{Convert.ToInt64(value, CultureInfo.InvariantCulture)}";

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Date(DateTime? value) => value is { } v ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "none";

    private static string? Change(string what, string before, string after) => before == after ? null : $"{what} {before} -> {after}";

    private static string? Join(params string?[] changes) =>
        string.Join("; ", changes.Where(c => c is not null)) is { Length: > 0 } joined ? joined : null;
}
