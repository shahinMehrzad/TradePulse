namespace Domain.Extensions;

public static class DateTimeExtensions
{
    /// <summary>
    /// Reads a <see cref="DateTime"/> as UTC, the one time zone this project stores. A JSON date arrives as <c>Utc</c> ("…Z"),
    /// <c>Unspecified</c> (no offset, taken to already be UTC) or <c>Local</c> (an offset, already converted to the server's zone).
    /// </summary>
    public static DateTime AsUtc(this DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
