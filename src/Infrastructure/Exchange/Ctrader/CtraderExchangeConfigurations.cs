namespace Infrastructure.Exchange.Ctrader;

public enum CtraderEnvironment
{
    /// <summary>The play-money accounts (<c>demo.ctraderapi.com</c>).</summary>
    Demo,

    /// <summary>The real accounts (<c>live.ctraderapi.com</c>).</summary>
    Live,
}

/// <summary>
/// How to reach a broker through the cTrader Open API and as whom. The secrets (<see cref="ClientSecret"/>, <see cref="AccessToken"/>) come from
/// configuration only (User Secrets, environment, <c>deploy/.env</c>), never from appsettings or the database, and are never written to a log or
/// an error text. The token for data is one made with the <c>accounts</c> scope (read only); a token that may trade belongs to the trading service.
/// </summary>
public sealed class CtraderExchangeConfigurations
{
    public const string SectionName = "CtraderExchangeConfigurations";

    public CtraderEnvironment Environment { get; set; } = CtraderEnvironment.Demo;

    /// <summary>The Client ID of the application registered at connect.spotware.com.</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The access token the account owner gave the application.</summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The cTrader account id (<c>ctidTraderAccountId</c>), not the number the broker shows. Nullable so that an empty value (what deploy passes when
    /// there is none) reads as "not set" instead of stopping the program.
    /// </summary>
    public long? AccountId { get; set; }

    /// <summary>Replaces the host of <see cref="Environment"/> (a proxy, or a test server).</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 5035;

    /// <summary>Always on against cTrader; a test server without a certificate turns it off.</summary>
    public bool UseTls { get; set; } = true;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>cTrader closes a connection that is silent for 30 seconds; the proxy's own heartbeat goes out well before.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The limit is 5 per second per connection; one less leaves room for a clock that is not exact.</summary>
    public int HistoricalRequestsPerSecond { get; set; } = 4;

    public int OtherRequestsPerSecond { get; set; } = 40;

    public string EffectiveHost => !string.IsNullOrWhiteSpace(Host)
        ? Host
        : Environment == CtraderEnvironment.Live ? "live.ctraderapi.com" : "demo.ctraderapi.com";

    /// <summary>True when everything needed to sign in is there. Nothing in this section is needed unless a Pepperstone series is collected.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(AccessToken) && AccountId > 0;
}
