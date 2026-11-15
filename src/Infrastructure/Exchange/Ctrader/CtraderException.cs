namespace Infrastructure.Exchange.Ctrader;

/// <summary>
/// A call to cTrader that did not come back with an answer: the other side said no (<see cref="ErrorCode"/>), or the connection failed. The
/// message is fit for a log: it holds no secret and no address.
/// </summary>
public sealed class CtraderException(string message, string? errorCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>cTrader's own name for what went wrong (<c>CH_CLIENT_AUTH_FAILURE</c>), null for a failure on this side.</summary>
    public string? ErrorCode { get; } = errorCode;

    /// <summary>When a payload type is blocked for a while (a rate limit hit), how long it stays so.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
