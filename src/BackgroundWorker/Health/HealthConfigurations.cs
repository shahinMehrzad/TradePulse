namespace BackgroundWorker.Health;

public class HealthConfigurations
{
    /// <summary>Serve /health over HTTP. Status changes are still written to the log when disabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where the endpoint listens. Localhost only by default; use http://0.0.0.0:8080 inside a container.</summary>
    public string Url { get; set; } = "http://localhost:5080";

    /// <summary>How often the connectivity probe pings Binance and checks the probe stream.</summary>
    public int ProbeIntervalSeconds { get; set; } = 30;

    /// <summary>A busy futures symbol used for the WebSocket liveness probe (its mark price ticks every few seconds).</summary>
    public string ProbeSymbol { get; set; } = "BTCUSDT";

    /// <summary>Connected but no liquidation for this long is reported as degraded. Liquidations are bursty, so keep it generous.</summary>
    public int LiquidationMaxSilenceMinutes { get; set; } = 60;
}
