namespace Application.Dtos.Dashboard;

public sealed record DashboardSeriesResponse(DateTime GeneratedAt, IReadOnlyList<DashboardSeriesItem> Items);

public record DashboardSeriesItem(
    string Exchange, string Market, string Symbol,
    IReadOnlyList<string> Timeframes, string DefaultTimeframe,
    DashboardSnapshot Current);

public sealed record DashboardSnapshot(
    string Timeframe, string Status, int? LagSeconds, DateTime? LastOpenTime,
    decimal? LastClose, decimal? ChangePercent, decimal? High, decimal? Low, decimal? Volume,
    DateTime? WindowStart, DateTime? WindowEnd, IReadOnlyList<decimal>? Sparkline);

public sealed record DashboardSeriesDetail(
    string Exchange, string Market, string Symbol,
    IReadOnlyList<string> Timeframes, string DefaultTimeframe,
    DashboardSnapshot Current,
    IReadOnlyList<DashboardRelatedData> Related)
    : DashboardSeriesItem(Exchange, Market, Symbol, Timeframes, DefaultTimeframe, Current);

public sealed record DashboardRelatedData(string DataType, string Status, int? LagSeconds, DateTime? LastTime);

public static class DashboardStatus
{
    public const string Ok = "ok";
    public const string Late = "late";
    public const string Empty = "empty";
}
