namespace Application.Exchange;

/// <summary>
/// What a call to an exchange came back with. A failed call is an expected outcome (rate limited, exchange down, network),
/// not an exception: the caller decides whether to retry, give up for now, or report it. Anything that is not an exchange
/// answer (a bug, cancellation) still throws.
/// </summary>
public sealed record ExchangeResult<T>
{
    private readonly T? _value;

    private ExchangeResult(bool success, T? value, string? error)
    {
        Success = success;
        _value = value;
        Error = error;
    }

    public bool Success { get; }

    /// <summary>Why it failed, in words fit for a log; never null on a failure of a known cause, but the exchange may give none.</summary>
    public string? Error { get; }

    /// <summary>The answer. Only valid when <see cref="Success"/>.</summary>
    public T Value => Success ? _value! : throw new InvalidOperationException($"The exchange call failed: {Error}");

    public static ExchangeResult<T> Ok(T value) => new(true, value, null);

    public static ExchangeResult<T> Fail(string? error) => new(false, default, error);
}

/// <summary>One answer of a paged exchange call.</summary>
/// <param name="HasMore">True when the answer was a full page, so asking again from where it ended may return more.</param>
public sealed record ExchangePage<T>(IReadOnlyList<T> Items, bool HasMore);
