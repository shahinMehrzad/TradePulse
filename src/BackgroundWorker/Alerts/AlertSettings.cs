namespace BackgroundWorker.Alerts;

public sealed class AlertSettings
{
    /// <summary>How many health reports in a row (one every 30 s) must agree before a change of a check is announced.</summary>
    public int ConfirmationsToAlert { get; set; } = 2;

    /// <summary>
    /// Same, for a degraded status only (a late candle, a slow answer): 6 reports = about 3 minutes. Short delays heal by themselves;
    /// an unhealthy status still uses <see cref="ConfirmationsToAlert"/>.
    /// </summary>
    public int ConfirmationsToAlertWhenDegraded { get; set; } = 6;

    /// <summary>At most this many messages an hour leave the collector; the rest are counted and announced once.</summary>
    public int MaxPerHour { get; set; } = 20;
}
