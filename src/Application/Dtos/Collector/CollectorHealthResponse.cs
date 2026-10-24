namespace Application.Dtos.Collector;

/// <summary>
/// What the collector says about itself, as the API relays it. <see cref="State"/> tells whether it could be asked at all:
/// <see cref="CollectorHealthState.Answered"/>, <see cref="CollectorHealthState.NotConfigured"/> or <see cref="CollectorHealthState.Unreachable"/>.
/// <see cref="Status"/> and <see cref="Checks"/> are only there when it answered.
/// </summary>
public sealed record CollectorHealthResponse(string State, string? Status, DateTime CheckedAt, IReadOnlyList<CollectorHealthCheck> Checks);

/// <summary>One component of the collector (database, a socket, data freshness...). <see cref="Status"/> is Healthy, Degraded or Unhealthy.</summary>
public sealed record CollectorHealthCheck(string Name, string Status, string? Description, IReadOnlyDictionary<string, string> Data);

public static class CollectorHealthState
{
    public const string Answered = "answered";
    public const string NotConfigured = "notConfigured";
    public const string Unreachable = "unreachable";
}
