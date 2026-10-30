using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>Second step of a normal sign-in: the code from the authenticator app, or a recovery code.</summary>
public class LoginTwoFactorModel(AdminLoginService loginService, PendingLoginCookie pendingLogin) : SignInPageModel(pendingLogin)
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Enter the code.")]
        public string Code { get; set; } = string.Empty;

        public bool UseRecoveryCode { get; set; }
    }

    public IActionResult OnGet() => PendingLogin.Read(HttpContext) is null ? SignInExpired() : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var pending = PendingLogin.Read(HttpContext);
        if (pending is null)
            return SignInExpired();
        if (!ModelState.IsValid)
            return Page();

        var outcome = await loginService.VerifyTwoFactorAsync(pending, Input.Code.Trim(), Input.UseRecoveryCode, cancellationToken);
        return await ContinueAsync(outcome, pending.ReturnUrl) ?? Page();
    }
}
