namespace Application.Signals;

/// <summary>How the service that takes signals judges a request. The secrets come from the environment or user secrets, never from a file in the repository.</summary>
public sealed record SignalIntakeOptions
{
    public const string SectionName = "Signals:Webhook";

    /// <summary>The secret of the sender and, while it is being changed, the one before it. At least one.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];

    /// <summary>How far the time of a request may be from ours.</summary>
    public TimeSpan MaxRequestSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How old the latest price of a symbol may be to judge a signal by it. Older: the answer is "try again", not a verdict.</summary>
    public TimeSpan MaxPriceAge { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The largest body taken. A signal is a few hundred bytes.</summary>
    public int MaxBodyBytes { get; init; } = 16 * 1024;
}
