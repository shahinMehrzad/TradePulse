namespace Application.Dtos.Dashboard;

public sealed record DashboardSeriesResponse(DateTime GeneratedAt, IReadOnlyList<DashboardSeriesItem> Items);

public sealed record DashboardSeriesItem(
    string Exchange, string Market, string Symbol,
    IReadOnlyList<string> Timeframes, string DefaultTimeframe,
    DashboardSnapshot Current);

public sealed record DashboardSnapshot(
    string Timeframe, string Status, int? LagSeconds, DateTime? LastOpenTime,
    decimal? LastClose, decimal? ChangePercent, decimal? High, decimal? Low, decimal? Volume,
    IReadOnlyList<decimal>? Sparkline);

public static class DashboardStatus
{
    public const string Ok = "ok";
    public const string Late = "late";
    public const string Empty = "empty";
}
