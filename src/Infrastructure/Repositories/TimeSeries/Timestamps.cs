namespace Infrastructure.Repositories.TimeSeries;

/// <summary>
/// Time columns are <c>timestamp</c> (no zone) and always hold UTC. Npgsql rejects <c>Kind=Utc</c> for that type,
/// so the kind is stripped on the way in and re-applied on the way out instead of letting the driver convert
/// through the session time zone.
/// </summary>
internal static class Timestamps
{
    public static DateTime ToTimestamp(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    public static DateTime FromTimestamp(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
