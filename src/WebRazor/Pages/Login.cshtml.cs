using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

public class LoginModel(AdminLoginService loginService, PendingLoginCookie pendingLogin) : SignInPageModel(pendingLogin)
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Notice { get; private set; }

    public class InputModel
    {
        [Required]
        public string Identifier { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect("/");

        Notice = TempData["LoginNotice"] as string;

        // Starting over: a half-finished sign-in is forgotten.
        PendingLogin.Clear(HttpContext);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return Page();

        var outcome = await loginService.LoginAsync(Input.Identifier.Trim(), Input.Password, cancellationToken);
        return await ContinueAsync(outcome, ReturnUrl) ?? Page();
    }
}
