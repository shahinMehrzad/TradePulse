using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebRazor.Api;

namespace WebRazor.Pages;

public class LoginModel(AdminLoginService loginService) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public class InputModel
    {
        [Required]
        public string Identifier { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect("/") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return Page();

        var outcome = await loginService.LoginAsync(Input.Identifier.Trim(), Input.Password, cancellationToken);
        if (outcome.Failure != LoginFailure.None)
        {
            // Wrong password and "not an admin" read the same on purpose: don't tell a stranger which accounts exist.
            Error = outcome.Failure switch
            {
                LoginFailure.TooManyAttempts => "Too many attempts. Wait a minute and try again.",
                LoginFailure.ApiUnavailable => "The API is not reachable right now.",
                _ => "Invalid credentials."
            };
            return Page();
        }

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

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
    }
}
