using Infrastructure.Persistence;

namespace Infrastructure.Identity;

internal static class ApplicationUserExtensions
{
    /// <summary>
    /// A disabled user is locked out until the end of time, using Identity's own lockout. A lockout from too many wrong
    /// passwords ends by itself and is not "disabled".
    /// </summary>
    public static readonly DateTimeOffset DisabledUntil = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public static bool IsDisabled(this ApplicationUser user) => user.LockoutEnd is { } end && end >= DisabledUntil;
}
