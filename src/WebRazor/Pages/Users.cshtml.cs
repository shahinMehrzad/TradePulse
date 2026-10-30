using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>The accounts of the panel: add an administrator, hand out a new one-time password, reset two-factor, disable or enable.</summary>
public class UsersModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    public IReadOnlyList<UserRow> Users { get; private set; } = [];

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Done { get; private set; }

    /// <summary>The password the API generated, shown once; it is not kept anywhere after this page is rendered.</summary>
    public OneTimePassword? Password { get; private set; }

    [BindProperty]
    public NewAdministrator Input { get; set; } = new();

    public class NewAdministrator
    {
        [Required(ErrorMessage = "Enter a user name."), StringLength(64)]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "That is not an email address.")]
        public string Email { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!IsConfirmed())
            return ConfirmFirst("/Users");

        Done = TempData["Done"] as string;
        if (TempData["PasswordUser"] is string userName && TempData["PasswordValue"] is string password)
            Password = new OneTimePassword(Guid.Empty, userName, password);

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return await LoadAsync(cancellationToken) ?? Page();

        var (result, password) = await api.CreateAdministratorAsync(Input.UserName.Trim(), Input.Email.Trim(), cancellationToken);
        return await FinishAsync(result, password, $"Administrator {Input.UserName.Trim()} was added.", cancellationToken);
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(Guid id, CancellationToken cancellationToken)
    {
        var (result, password) = await api.ResetPasswordAsync(id, cancellationToken);
        return await FinishAsync(result, password, "The password was reset.", cancellationToken);
    }

    public async Task<IActionResult> OnPostResetTwoFactorAsync(Guid id, CancellationToken cancellationToken) =>
        await FinishAsync(await api.ResetTwoFactorAsync(id, cancellationToken), null, "Two-factor authentication was reset; the user sets it up again at the next sign-in.", cancellationToken);

    public async Task<IActionResult> OnPostDisableAsync(Guid id, CancellationToken cancellationToken) =>
        await FinishAsync(await api.SetUserDisabledAsync(id, true, cancellationToken), null, "The user was disabled and signed out.", cancellationToken);

    public async Task<IActionResult> OnPostEnableAsync(Guid id, CancellationToken cancellationToken) =>
        await FinishAsync(await api.SetUserDisabledAsync(id, false, cancellationToken), null, "The user was enabled.", cancellationToken);

    /// <summary>Redirects on success (so a refresh cannot repeat the change); shows the refusal otherwise.</summary>
    private async Task<IActionResult> FinishAsync(ApiResult result, OneTimePassword? password, string done, CancellationToken cancellationToken)
    {
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (result.StepUpRequired)
            return ConfirmFirst("/Users");

        if (!result.Success)
        {
            Errors = result.Errors;
            return await LoadAsync(cancellationToken) ?? Page();
        }

        TempData["Done"] = done;
        if (password is not null)
        {
            TempData["PasswordUser"] = password.UserName;
            TempData["PasswordValue"] = password.TemporaryPassword;
        }

        return RedirectToPage();
    }

    /// <returns>A result to return when the page cannot be shown; null when the list was loaded.</returns>
    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var users = await api.GetUsersAsync(cancellationToken);
            if (users is null)
                return await EndSessionAsync();

            Users = users;
        }
        catch (HttpRequestException)
        {
            Errors = ["The API is not reachable right now."];
        }

        return null;
    }
}
