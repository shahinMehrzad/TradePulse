namespace Application.Queries.Candles;

/// <summary>A read request the caller got wrong (bad limit, inverted range). Maps to HTTP 400.</summary>
public sealed class InvalidCandleQueryException(string parameter, string message) : Exception(message)
{
    public string Parameter { get; } = parameter;
}
