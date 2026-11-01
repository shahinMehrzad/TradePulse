using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// The administrator's own keys for the exchange. The private half is made and kept by the Trader program and is never shown or typed:
/// what the user does is paste the public key into Binance and bring back the API key Binance made for it.
/// </summary>
public class ExchangeKeysModel(TraderApiClient trader, IOptions<TraderOptions> options, SessionTokenStore store) : AdminPageModel(store)
{
    private const string Path = "/ExchangeKeys";

    public IReadOnlyList<ExchangeKeyInfo> Keys { get; private set; } = [];

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Done { get; private set; }

    public bool Configured => options.Value.Enabled;

    [BindProperty]
    public NewKeyForm Input { get; set; } = new();

    [BindProperty]
    public string? ApiKey { get; set; }

    public class NewKeyForm
    {
        [Required(ErrorMessage = "Give the key a name."), StringLength(50)]
        public string Label { get; set; } = string.Empty;

        /// <summary>Live (real money) or Demo (Binance's play-money environment). Fixed once the key is made.</summary>
        [RegularExpression("Live|Demo", ErrorMessage = "Choose Live or Demo.")]
        public string Environment { get; set; } = "Demo";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (Configured && !IsConfirmed())
            return ConfirmFirst(Path);

        Done = TempData["Done"] as string;
        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return await LoadAsync(cancellationToken) ?? Page();

        return await FinishAsync(await trader.CreateAsync(Input.Label.Trim(), Input.Environment, cancellationToken),
            "The key pair was made. Now paste the public key into Binance (step 1 below).", cancellationToken);
    }

    public async Task<IActionResult> OnPostSetApiKeyAsync(Guid id, CancellationToken cancellationToken) =>
        await FinishAsync(await trader.SetApiKeyAsync(id, ApiKey?.Trim() ?? "", cancellationToken), "The API key was checked with Binance. See the result below.", cancellationToken);

    public async Task<IActionResult> OnPostVerifyAsync(Guid id, CancellationToken cancellationToken) =>
        await FinishAsync(await trader.VerifyAsync(id, cancellationToken), "The key was checked with Binance again. See the result below.", cancellationToken);

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await FinishAsync(await trader.DeleteAsync(id, cancellationToken), "The key was deleted, and with it its private key. Delete the API key at Binance too.", cancellationToken);

    /// <summary>Redirects on success (so a refresh cannot repeat the change); shows the refusal otherwise.</summary>
    private async Task<IActionResult> FinishAsync(ApiResult result, string done, CancellationToken cancellationToken)
    {
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (result.StepUpRequired)
            return ConfirmFirst(Path);

        if (!result.Success)
        {
            Errors = result.Errors;
            return await LoadAsync(cancellationToken) ?? Page();
        }

        TempData["Done"] = done;
        return RedirectToPage();
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!Configured)
            return null;

        try
        {
            var keys = await trader.GetKeysAsync(cancellationToken);
            if (keys is null)
                return await EndSessionAsync();

            Keys = keys;
        }
        catch (HttpRequestException)
        {
            Errors = ["The key vault is not reachable right now."];
        }

        return null;
    }
}
