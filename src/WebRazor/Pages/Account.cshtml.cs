using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>The signed-in user's own account: change the password and make new recovery codes.</summary>
public class AccountModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public AccountInfo? Info { get; private set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Done { get; private set; }

    /// <summary>Shown once, right after they were made; not kept anywhere.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    [BindProperty]
    public PasswordForm Password { get; set; } = new();

    [BindProperty]
    public CodesForm Codes { get; set; } = new();

    public class PasswordForm
    {
        [Required(ErrorMessage = "Enter your current password."), DataType(DataType.Password)]
        public string Current { get; set; } = string.Empty;

        [Required(ErrorMessage = "Choose a new password."), DataType(DataType.Password)]
        public string New { get; set; } = string.Empty;

        [Required(ErrorMessage = "Repeat the new password."), DataType(DataType.Password), Compare(nameof(New), ErrorMessage = "The two passwords differ.")]
        public string Repeat { get; set; } = string.Empty;
    }

    public class CodesForm
    {
        [Required(ErrorMessage = "Enter your password."), DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Done = TempData["Done"] as string;
        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken cancellationToken)
    {
        if (!FormIsValid(nameof(Password)))
            return await LoadAsync(cancellationToken) ?? Page();

        var result = await api.ChangeOwnPasswordAsync(Password.Current, Password.New, cancellationToken);
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (!result.Success)
        {
            Errors = result.Errors;
            return await LoadAsync(cancellationToken) ?? Page();
        }

        TempData["Done"] = "Your password was changed. Your other devices were signed out.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRecoveryCodesAsync(CancellationToken cancellationToken)
    {
        if (!FormIsValid(nameof(Codes)))
            return await LoadAsync(cancellationToken) ?? Page();

        var (result, codes) = await api.RegenerateRecoveryCodesAsync(Codes.Password, cancellationToken);
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (!result.Success || codes is null)
        {
            Errors = result.Errors;
            return await LoadAsync(cancellationToken) ?? Page();
        }

        RecoveryCodes = codes;
        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>The page has two forms and one model, so only the errors of the posted form's own fields matter.</summary>
    private bool FormIsValid(string prefix) =>
        !ModelState.Any(entry => entry.Key.StartsWith(prefix + ".", StringComparison.Ordinal) && entry.Value?.Errors.Count > 0);

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Info = await api.GetAccountAsync(cancellationToken);
            if (Info is null)
                return await EndSessionAsync();
        }
        catch (HttpRequestException)
        {
            Errors = ["The API is not reachable right now."];
        }

        return null;
    }
}
