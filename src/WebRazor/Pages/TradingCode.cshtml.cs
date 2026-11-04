using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using QRCoder;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// The trading code: a second entry in the authenticator app whose secret only the key vault has. The keys and the orders ask for a confirmation
/// that the vault gives after it, so that even the API, which can sign anybody in, cannot move a key or place an order. This page sets the code
/// up (the first time), takes it (to confirm for ten minutes) and starts again with a recovery code (a lost phone).
/// </summary>
public class TradingCodeModel(TraderApiClient trader, SessionTokenStore store) : AdminPageModel(store)
{
    private readonly SessionTokenStore _store = store;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? RecoveryCode { get; set; }

    public TradingCodeStatus? Status { get; private set; }

    /// <summary>The key in groups of four, to type it into an app that cannot read the QR code.</summary>
    public string? SharedKey { get; private set; }

    public string? QrSvg { get; private set; }

    /// <summary>Shown once, right after the setup. The vault keeps only their hashes.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter the code.")]
        public string Code { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Notice = TempData["TradingCodeNotice"] as string ?? TempData["TradingCodeDone"] as string;
        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>The first code: the app has the key. The vault answers with the recovery codes, which are shown here and nowhere else.</summary>
    public async Task<IActionResult> OnPostSetUpAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return await LoadAsync(cancellationToken) ?? Page();

        var (result, enrolled) = await trader.SetUpTradingCodeAsync(Input.Code.Trim(), cancellationToken);
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (!result.Success || enrolled is null)
        {
            Error = result.Errors.FirstOrDefault() ?? "That did not work. Try the next code.";
            return await LoadAsync(cancellationToken) ?? Page();
        }

        Remember(enrolled.ConfirmationToken, enrolled.ExpiresAt);
        RecoveryCodes = enrolled.RecoveryCodes;
        Status = new TradingCodeStatus(true, null, enrolled.RecoveryCodes.Length, enrolled.ExpiresAt);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return await LoadAsync(cancellationToken) ?? Page();

        var (result, confirmation) = await trader.ConfirmTradingCodeAsync(Input.Code.Trim(), cancellationToken);
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (!result.Success || confirmation is null)
        {
            Error = result.Errors.FirstOrDefault() ?? "That did not work. Try the next code.";
            return await LoadAsync(cancellationToken) ?? Page();
        }

        Remember(confirmation.Token, confirmation.ExpiresAt);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/ExchangeKeys");
    }

    /// <summary>Lost the phone: a recovery code forgets the code, and the next visit sets a new one up.</summary>
    public async Task<IActionResult> OnPostResetAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        var result = await trader.ResetTradingCodeAsync(RecoveryCode?.Trim() ?? string.Empty, cancellationToken);
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (!result.Success)
        {
            Error = result.Errors.FirstOrDefault() ?? "That did not work.";
            return await LoadAsync(cancellationToken) ?? Page();
        }

        if (User.FindFirstValue(ApiAuthHandler.SessionClaim) is { } sessionId)
            _store.ClearTraderConfirmation(sessionId);
        TempData["TradingCodeDone"] = "The trading code was forgotten. Set up a new one below, and keep the new recovery codes.";
        return RedirectToPage(new { returnUrl = ReturnUrl });
    }

    private void Remember(string token, DateTime expiresAt)
    {
        if (User.FindFirstValue(ApiAuthHandler.SessionClaim) is { } sessionId)
            _store.SetTraderConfirmation(sessionId, token, expiresAt);
    }

    /// <summary>Shows where the user stands; for one who has no code yet, also the key and the QR code of the setup.</summary>
    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = await trader.GetTradingCodeStatusAsync(cancellationToken);
            if (Status is null)
                return await EndSessionAsync();

            if (!Status.Enrolled)
            {
                var (result, setup) = await trader.BeginTradingCodeAsync(cancellationToken);
                if (result.SessionRejected)
                    return await EndSessionAsync();
                if (setup is null)
                {
                    Error ??= result.Errors.FirstOrDefault() ?? "The setup could not be started.";
                    return null;
                }

                SharedKey = string.Join(' ', setup.SharedKey.Chunk(4).Select(chunk => new string(chunk)));
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(setup.AuthenticatorUri, QRCodeGenerator.ECCLevel.M);
                QrSvg = new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
            }
        }
        catch (HttpRequestException)
        {
            Error = "The key vault is not reachable right now.";
        }

        return null;
    }
}
