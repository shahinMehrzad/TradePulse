using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace WebRazor.Api;

/// <summary>
/// Keeps a <see cref="PendingLogin"/> between the pages of a sign-in. The cookie is encrypted and signed by data protection and
/// valid for thirty minutes (as long as the API's challenge token: time enough to pick a password and install an authenticator app), so the browser can neither read nor forge it.
/// </summary>
public sealed class PendingLoginCookie(IDataProtectionProvider dataProtection, IWebHostEnvironment environment)
{
    public const string Name = "tradepulse.login";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector("tradepulse.pending-login").ToTimeLimitedDataProtector();

    public void Save(HttpContext context, PendingLogin pending) =>
        context.Response.Cookies.Append(Name, _protector.Protect(JsonSerializer.Serialize(pending), Lifetime), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            // Plain http is only tolerated in Development, like the panel's own cookie.
            Secure = !environment.IsDevelopment() || context.Request.IsHttps,
            MaxAge = Lifetime,
            IsEssential = true
        });

    public PendingLogin? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(Name, out var value))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PendingLogin>(_protector.Unprotect(value));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null; // expired, tampered with, or from before a key change
        }
    }

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(Name);
}
