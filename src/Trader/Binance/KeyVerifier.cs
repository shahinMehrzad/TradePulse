using System.Text.Json;
using Microsoft.Extensions.Options;
using Trader.Vault;

namespace Trader.Binance;

public sealed record VerificationResult(IReadOnlyList<string> Problems)
{
    public bool Accepted => Problems.Count == 0;
}

/// <summary>
/// Asks Binance whether a key is one this program is willing to hold: (1) its signatures are accepted by the Futures API, then
/// (2) Binance's own list of what the key may do says <b>no withdrawals, no transfers, locked to an IP, futures on</b>.
/// Both are read-only calls. The second fails closed: if it cannot be answered the key is not accepted, because "I could not check
/// that it cannot take the money out" is not good enough.
/// </summary>
public sealed class KeyVerifier(BinanceClient binance, IOptions<BinanceOptions> options)
{
    public async Task<VerificationResult> VerifyAsync(IRequestSigner signer, string apiKey, KeyEnvironment environment, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        var futures = await binance.GetSignedAsync(settings.FuturesUrl(environment), "/fapi/v2/balance", apiKey, signer, cancellationToken);
        if (!futures.Ok)
            return new VerificationResult([Explain(futures, "Futures")]);

        // Demo has no Wallet API and no real money: the permission check is for live keys only.
        if (environment == KeyEnvironment.Demo || !settings.RequirePermissionCheck)
            return new VerificationResult([]);

        var permissions = await binance.GetSignedAsync(settings.SpotBaseUrl, "/sapi/v1/account/apiRestrictions", apiKey, signer, cancellationToken);
        if (!permissions.Ok || permissions.Body is not { ValueKind: JsonValueKind.Object } body)
            return new VerificationResult([$"Could not confirm what this key may do ({Explain(permissions, "the permissions check")}). A key is only accepted when Binance confirms that withdrawals are off."]);

        var problems = new List<string>();

        // The forbidden rights must be explicitly off: a field Binance left out does not count as "off".
        if (Flag(body, "enableWithdrawals") != false)
            problems.Add("Withdrawals are enabled for this key (or Binance did not say). Turn off \"Enable Withdrawals\" in the key's restrictions at Binance.");
        if (Flag(body, "enableInternalTransfer") != false || Flag(body, "permitsUniversalTransfer") != false)
            problems.Add("Transfers between accounts are enabled for this key (or Binance did not say). Turn off \"Enable Internal Transfer\" and \"Permits Universal Transfer\" at Binance.");
        if (Flag(body, "ipRestrict") != true)
            problems.Add("The key is not restricted to trusted IPs. Choose \"Restrict access to trusted IPs only\" at Binance and add the address of this server.");
        if (Flag(body, "enableFutures") != true)
            problems.Add("Futures trading is not enabled for this key. Turn on \"Enable Futures\" at Binance.");

        return new VerificationResult(problems);
    }

    /// <returns>true or false as Binance said it, or null when the field is missing or not a boolean.</returns>
    private static bool? Flag(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.ValueKind == JsonValueKind.True : null;

    private static string Explain(BinanceReply reply, string what) => reply switch
    {
        { Status: 0 } => $"Could not reach Binance ({what}). Try again in a moment.",
        { Code: -1022 } => "Binance did not accept the signature. Check that the public key shown here is the one you gave Binance for this API key.",
        { Code: -2014 } => "That does not look like an API key from Binance. Check what you pasted.",
        { Code: -2008 } => "Binance does not know this API key. Check that it is the one made for this public key, in the right environment (live, not demo).",
        { Code: -2015 } => "Binance refused the key: the IP restriction does not include this server, or \"Enable Reading\" / \"Enable Futures\" is off.",
        { Code: -1021 } => "The clock of this server is off, so Binance refused the request.",
        { Code: { } code } => $"Binance answered with error {code} ({what}).",
        _ => $"Binance answered with HTTP {reply.Status} ({what})."
    };
}
