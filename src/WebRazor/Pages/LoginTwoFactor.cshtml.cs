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

        /// <summary>Authenticator (the app), Email or Recovery.</summary>
        public string Method { get; set; } = "Authenticator";
    }

    public static readonly string[] Methods = ["Authenticator", "Email", "Recovery"];

    public bool EmailCodeAvailable { get; private set; }

    public string? Notice { get; private set; }

    public IActionResult OnGet()
    {
        var pending = PendingLogin.Read(HttpContext);
        if (pending is null)
            return SignInExpired();

        EmailCodeAvailable = pending.EmailCodeAvailable;
        return Page();
    }

    /// <summary>The "send me a code by email" button: no code has been typed yet, so the form is not validated.</summary>
    public async Task<IActionResult> OnPostSendEmailAsync(CancellationToken cancellationToken)
    {
        var pending = PendingLogin.Read(HttpContext);
        if (pending is null)
            return SignInExpired();

        EmailCodeAvailable = pending.EmailCodeAvailable;
        var outcome = await loginService.SendEmailCodeAsync(pending, cancellationToken);
        ModelState.Clear(); // the code box is empty on purpose
        if (outcome.Failure == LoginFailure.None)
        {
            Notice = "We sent a code to your email address. It works for a few minutes.";
            Input.Method = "Email";
        }
        else
        {
            Error = Describe(outcome);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var pending = PendingLogin.Read(HttpContext);
        if (pending is null)
            return SignInExpired();
        EmailCodeAvailable = pending.EmailCodeAvailable;
        if (!ModelState.IsValid)
            return Page();

        var method = Methods.Contains(Input.Method) ? Input.Method : "Authenticator";
        var outcome = await loginService.VerifyTwoFactorAsync(pending, Input.Code.Trim(), method, cancellationToken);
        return await ContinueAsync(outcome, pending.ReturnUrl) ?? Page();
    }
}
