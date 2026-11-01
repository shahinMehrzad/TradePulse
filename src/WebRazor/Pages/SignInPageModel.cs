using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>Base of the pages a sign-in passes through: what each outcome of a step means for the browser.</summary>
public abstract class SignInPageModel(PendingLoginCookie pendingLogin) : PageModel
{
    protected PendingLoginCookie PendingLogin { get; } = pendingLogin;

    /// <summary>The half-finished sign-in is gone (it timed out, or the cookie was cleared): back to the start, saying why.</summary>
    protected IActionResult SignInExpired()
    {
        TempData["LoginNotice"] = "Your sign-in timed out, so nothing was changed. Sign in again.";
        return RedirectToPage("/Login");
    }

    public string? Error { get; protected set; }

    /// <summary>Opens the panel session. The pending-login cookie has done its job.</summary>
    protected async Task SignInAsync(LoginOutcome outcome)
    {
        PendingLogin.Clear(HttpContext);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, outcome.Admin!.UserName),
            new(ApiAuthHandler.SessionClaim, outcome.SessionId!)
        };
        claims.AddRange(outcome.Admin.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false });
    }

    /// <summary>Follows the outcome of a step: on to the next page of the sign-in, or into the panel. Null when the step failed (<see cref="Error"/> says why).</summary>
    protected async Task<IActionResult?> ContinueAsync(LoginOutcome outcome, string? returnUrl)
    {
        if (outcome.Failure != LoginFailure.None)
        {
            Error = Describe(outcome);
            return null;
        }

        switch (outcome.Next)
        {
            case LoginNext.SignedIn:
                await SignInAsync(outcome);
                return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
            case LoginNext.TwoFactor:
                PendingLogin.Save(HttpContext, outcome.Pending! with { ReturnUrl = returnUrl });
                return RedirectToPage("/LoginTwoFactor");
            default:
                PendingLogin.Save(HttpContext, outcome.Pending! with { ReturnUrl = returnUrl });
                return RedirectToPage("/LoginSetup");
        }
    }

    protected static string Describe(LoginOutcome outcome) => outcome.Failure switch
    {
        LoginFailure.TooManyAttempts => "Too many attempts. Wait a minute and try again.",
        LoginFailure.ApiUnavailable => "The API is not reachable right now.",
        // Wrong password and "not an admin" read the same on purpose: don't tell a stranger which accounts exist.
        LoginFailure.InvalidCredentials or LoginFailure.NotAnAdmin => "Invalid credentials.",
        _ => outcome.Message ?? "That did not work. Sign in again."
    };
}
