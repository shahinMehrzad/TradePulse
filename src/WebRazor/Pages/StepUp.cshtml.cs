using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// Asks a signed-in user for a fresh second-factor code before something that matters (managing users, later the exchange keys
/// and trading). The proof lasts ten minutes and stays on the server with the session.
/// </summary>
public class StepUpModel(TradePulseApiClient api, SessionTokenStore store) : AdminPageModel(store)
{
    private readonly SessionTokenStore _store = store;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool EmailCodeAvailable { get; private set; }

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter the code.")]
        public string Code { get; set; } = string.Empty;

        /// <summary>Authenticator (the app) or Email.</summary>
        public string Method { get; set; } = "Authenticator";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Notice = TempData["StepUpNotice"] as string;
        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>The "email me a code" button: nothing has been typed yet, so the form is not validated.</summary>
    public async Task<IActionResult> OnPostSendEmailAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        var result = await api.SendStepUpEmailCodeAsync(cancellationToken);
        if (result.SessionRejected)
            return await EndSessionAsync();

        if (result.Success)
        {
            Notice = "We sent a code to your email address. It works for a few minutes.";
            Input.Method = "Email";
        }
        else
        {
            Error = result.Errors.FirstOrDefault() ?? "The email could not be sent.";
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return await LoadAsync(cancellationToken) ?? Page();

        var method = Input.Method == "Email" ? "Email" : "Authenticator";
        var (result, grant) = await api.StepUpAsync(Input.Code.Trim(), method, cancellationToken);
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (!result.Success || grant is null)
        {
            Error = result.Errors.FirstOrDefault() ?? "That did not work. Try the next code.";
            return await LoadAsync(cancellationToken) ?? Page();
        }

        if (User.FindFirstValue(ApiAuthHandler.SessionClaim) is { } sessionId)
            _store.SetElevation(sessionId, grant.Token, grant.ExpiresAt);

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var account = await api.GetAccountAsync(cancellationToken);
            if (account is null)
                return await EndSessionAsync();

            EmailCodeAvailable = account is { EmailCodesEnabled: true, EmailAvailable: true };
        }
        catch (HttpRequestException)
        {
            Error = "The API is not reachable right now.";
        }

        return null;
    }
}
