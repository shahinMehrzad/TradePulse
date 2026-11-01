using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using QRCoder;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// What a user does once, before the panel opens: replace the password they were given, then put the key in an authenticator
/// app and prove it works with the first code. Finishing shows the recovery codes, this once.
/// </summary>
public class LoginSetupModel(AdminLoginService loginService, PendingLoginCookie pendingLogin) : SignInPageModel(pendingLogin)
{
    public PendingLogin? Pending { get; private set; }

    public bool ChangingPassword => Pending?.MustChangePassword == true;

    /// <summary>The key in groups of four, to type into an app that cannot scan.</summary>
    public string? SharedKey { get; private set; }

    /// <summary>The QR code as inline SVG (the panel's CSP allows no remote or script images).</summary>
    public string? QrSvg { get; private set; }

    /// <summary>Set after two-factor was turned on: the page shows them instead of a form.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    [BindProperty]
    public PasswordForm Password { get; set; } = new();

    [BindProperty]
    public CodeForm Code { get; set; } = new();

    public class PasswordForm
    {
        [Required(ErrorMessage = "Enter the password you were given."), DataType(DataType.Password)]
        public string Current { get; set; } = string.Empty;

        [Required(ErrorMessage = "Choose a new password."), DataType(DataType.Password)]
        public string New { get; set; } = string.Empty;

        [Required(ErrorMessage = "Repeat the new password."), DataType(DataType.Password), Compare(nameof(New), ErrorMessage = "The two passwords differ.")]
        public string Repeat { get; set; } = string.Empty;
    }

    public class CodeForm
    {
        [Required(ErrorMessage = "Enter the code the app shows.")]
        public string Value { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Pending = PendingLogin.Read(HttpContext);
        if (Pending is null)
            return SignInExpired();

        return await ShowAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken cancellationToken)
    {
        Pending = PendingLogin.Read(HttpContext);
        if (Pending is null)
            return SignInExpired();
        if (!FormIsValid(nameof(Password)))
            return await ShowAsync(cancellationToken);

        var outcome = await loginService.ChangePasswordAsync(Pending, Password.Current, Password.New, cancellationToken);
        return await ContinueAsync(outcome, Pending.ReturnUrl) ?? await ShowAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        Pending = PendingLogin.Read(HttpContext);
        if (Pending is null)
            return SignInExpired();
        if (!FormIsValid(nameof(Code)))
            return await ShowAsync(cancellationToken);

        var outcome = await loginService.ConfirmTwoFactorAsync(Pending, Code.Value.Trim(), cancellationToken);
        if (outcome.Failure != LoginFailure.None)
        {
            Error = Describe(outcome);
            return await ShowAsync(cancellationToken);
        }

        await SignInAsync(outcome);
        RecoveryCodes = outcome.RecoveryCodes ?? [];
        return Page();
    }

    /// <summary>
    /// The page has two forms and one model, and the fields of the form that was not posted would otherwise count as missing,
    /// so only the errors of the posted form's own fields matter.
    /// </summary>
    private bool FormIsValid(string prefix) =>
        !ModelState.Any(entry => entry.Key.StartsWith(prefix + ".", StringComparison.Ordinal) && entry.Value?.Errors.Count > 0);

    /// <summary>Renders the step the user is on; the key is fetched here so a wrong code can simply be tried again.</summary>
    private async Task<IActionResult> ShowAsync(CancellationToken cancellationToken)
    {
        if (ChangingPassword || !Pending!.NeedsTwoFactorSetup)
            return Page();

        var enrollment = await loginService.BeginTwoFactorAsync(Pending, cancellationToken);
        if (enrollment.Enrollment is null)
        {
            Error = enrollment.Failure == LoginFailure.ApiUnavailable
                ? "The API is not reachable right now."
                : enrollment.Message ?? "The sign-in has expired. Sign in again.";
            return Page();
        }

        SharedKey = string.Join(' ', enrollment.Enrollment.SharedKey.Chunk(4).Select(chunk => new string(chunk)));
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(enrollment.Enrollment.AuthenticatorUri, QRCodeGenerator.ECCLevel.M);
        QrSvg = new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
        return Page();
    }
}
