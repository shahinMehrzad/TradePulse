using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebRazor.Api;

namespace WebRazor.Pages;

/// <summary>
/// Manual market orders on the administrator's own keys, with the limits the Trader enforces shown next to the form, and the halt
/// switch. The panel only forwards: every rule (symbols, caps, halt, Demo or Live) is decided by the Trader, which does not trust this page.
/// </summary>
public class TradingModel(TraderApiClient trader, IOptions<TraderOptions> options, SessionTokenStore store) : AdminPageModel(store)
{
    private const string Path = "/Trading";

    public IReadOnlyList<ExchangeKeyInfo> UsableKeys { get; private set; } = [];

    public IReadOnlyList<TradingOrder> Orders { get; private set; } = [];

    public TradingState? State { get; private set; }

    /// <summary>Every symbol the server allows, and whether it is enabled: the choice is the same for every administrator.</summary>
    public IReadOnlyList<TradingSymbol> Symbols { get; private set; } = [];

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string? Done { get; private set; }

    public bool Configured => options.Value.Enabled;

    [BindProperty]
    public OrderForm Input { get; set; } = new();

    public class OrderForm
    {
        [Required(ErrorMessage = "Choose a key.")]
        public Guid? KeyId { get; set; }

        [Required(ErrorMessage = "Choose a symbol.")]
        public string Symbol { get; set; } = string.Empty;

        [RegularExpression("BUY|SELL", ErrorMessage = "Choose Buy or Sell.")]
        public string Side { get; set; } = "BUY";

        [Required(ErrorMessage = "Enter the quantity."), Range(0.00000001, 1000000, ErrorMessage = "Enter a quantity above 0.")]
        public decimal? Quantity { get; set; }

        /// <summary>The order may only shrink a position; it is allowed while trading is halted.</summary>
        public bool ReduceOnly { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Done = TempData["Done"] as string;
        return await LoadAsync(cancellationToken) ?? Page();
    }

    public async Task<IActionResult> OnPostOrderAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return await LoadAsync(cancellationToken) ?? Page();

        return await FinishAsync(
            await trader.PlaceOrderAsync(Input.KeyId!.Value, Input.Symbol, Input.Side, Input.Quantity!.Value, Input.ReduceOnly, cancellationToken),
            "The order was sent. See its result in the list below.", cancellationToken);
    }

    /// <summary>Enabling a symbol for trading asks for a code (the Trader says so); disabling is one click, like the halt.</summary>
    public async Task<IActionResult> OnPostEnableSymbolAsync(string symbol, CancellationToken cancellationToken) =>
        await FinishAsync(await trader.SetSymbolEnabledAsync(symbol, true, cancellationToken),
            $"{symbol} is enabled for trading. Its price watch is on, too: you are told about sudden moves.", cancellationToken);

    public async Task<IActionResult> OnPostDisableSymbolAsync(string symbol, CancellationToken cancellationToken) =>
        await FinishAsync(await trader.SetSymbolEnabledAsync(symbol, false, cancellationToken),
            $"{symbol} is no longer enabled for trading: no new position can be opened in it. A position you hold can still be closed.", cancellationToken);

    public async Task<IActionResult> OnPostHaltAsync(CancellationToken cancellationToken) =>
        await FinishAsync(await trader.HaltAsync(cancellationToken), "Trading is halted: no new positions can be opened.", cancellationToken);

    public async Task<IActionResult> OnPostResumeAsync(CancellationToken cancellationToken) =>
        await FinishAsync(await trader.ResumeAsync(cancellationToken), "Trading is resumed.", cancellationToken);

    private async Task<IActionResult> FinishAsync(ApiResult result, string done, CancellationToken cancellationToken)
    {
        if (result.SessionRejected)
            return await EndSessionAsync();
        if (result.StepUpRequired)
            return ConfirmWithTradingCode(Path);

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
            State = await trader.GetTradingStateAsync(cancellationToken);
            if (State is null)
                return await EndSessionAsync();

            Symbols = await trader.GetSymbolsAsync(cancellationToken) ?? [];

            // Only a key Binance has accepted can trade, and a live one only when the server owner allows it.
            UsableKeys = (await trader.GetKeysAsync(cancellationToken) ?? [])
                .Where(key => key.Status == "Active" && (key.IsDemo || State.LiveAllowed)).ToList();
            Orders = await trader.GetOrdersAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            Errors = ["The trading service is not reachable right now."];
        }

        return null;
    }
}
