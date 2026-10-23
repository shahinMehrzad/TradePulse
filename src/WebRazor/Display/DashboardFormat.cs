using System.Globalization;

namespace WebRazor.Display;

/// <summary>How the dashboard writes prices, changes and durations. Always the invariant culture: the panel is not localized.</summary>
public static class DashboardFormat
{
    /// <summary>More decimals for cheaper coins: 67,250.5 / 168.40 / 0.6231.</summary>
    public static string Price(decimal value) => value.ToString(value >= 1000m ? "N1" : value >= 1m ? "N2" : "N4", CultureInfo.InvariantCulture);

    public static string Change(decimal percent) => percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";

    /// <summary>"4 min", "1 h 4 min", "2 d 3 h": the two largest units, never more.</summary>
    public static string Lag(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalMinutes < 1)
            return "under 1 min";
        if (span.TotalHours < 1)
            return $"{span.Minutes} min";
        if (span.TotalDays < 1)
            return span.Minutes == 0 ? $"{span.Hours} h" : $"{span.Hours} h {span.Minutes} min";

        return span.Hours == 0 ? $"{(int)span.TotalDays} d" : $"{(int)span.TotalDays} d {span.Hours} h";
    }

    /// <summary>How much time a card's figures cover: "6 h" for 24 candles of 15 minutes.</summary>
    public static string Window(DateTime start, DateTime end) => Lag((int)(end - start).TotalSeconds);

    /// <summary>"just now", "7 min ago", "1 h 4 min ago": how long ago the newest candle closed.</summary>
    public static string Ago(int seconds) => seconds < 60 ? "just now" : Lag(seconds) + " ago";

    /// <summary>A moment on a chart's edge, UTC: "10-23 09:15", or just the date for time frames of a day or more.</summary>
    public static string ChartTime(DateTime time, string timeframe) =>
        time.ToString(timeframe.EndsWith('d') || timeframe.EndsWith('w') ? "MM-dd" : "MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The name the panel gives the API's data type words.</summary>
    public static string DataTypeLabel(string dataType) => dataType switch
    {
        "fundingrate" => "Funding rate",
        "openinterest" => "Open interest",
        "liquidation" => "Liquidations",
        _ => dataType
    };

    /// <summary>Tabler badge colours for the API's status words.</summary>
    public static string StatusBadge(string status) => status switch
    {
        "ok" => "bg-success-lt text-success",
        "late" => "bg-warning-lt text-warning",
        _ => "bg-secondary-lt text-secondary"
    };

    public static string StatusLabel(string status) => status switch
    {
        "ok" => "OK",
        "late" => "Late",
        "empty" => "Empty",
        _ => status
    };
}
