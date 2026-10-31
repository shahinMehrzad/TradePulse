namespace Domain.Exceptions;

/// <summary>The exchange could not be asked (down, rate limited, network), so a request that needs its answer cannot be decided now.</summary>
public sealed class ExchangeUnavailableException(string message) : Exception(message);
