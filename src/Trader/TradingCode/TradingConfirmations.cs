using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Trader.TradingCode;

/// <summary>
/// The confirmations the vault itself gave, after the trading code of a user: a token good for ten minutes, held in this program's memory only
/// (a restart asks again). It is what the keys and the orders ask for. The tokens of the API are not accepted for this: whoever can make those
/// (the API, and so a hacked one) must not be able to say "this user just confirmed".
/// </summary>
public sealed class TradingConfirmations(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (Guid User, DateTimeOffset Expires)> _tokens = new();

    /// <summary>A new confirmation for the user. Only a hash of the token is kept here.</summary>
    public (string Token, DateTime ExpiresAt) Issue(Guid user)
    {
        var now = time.GetUtcNow();
        foreach (var (key, value) in _tokens)
            if (value.Expires <= now)
                _tokens.TryRemove(key, out _);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = now + Lifetime;
        _tokens[Hash(token)] = (user, expires);
        return (token, expires.UtcDateTime);
    }

    /// <summary>True when this token was given to this very user and has not run out.</summary>
    public bool IsValid(string? token, Guid user) =>
        !string.IsNullOrEmpty(token) && _tokens.TryGetValue(Hash(token), out var held) && held.User == user && held.Expires > time.GetUtcNow();

    /// <summary>When this user's confirmation ends (the longest of theirs), or null when they have none.</summary>
    public DateTime? ConfirmedUntil(Guid user)
    {
        var now = time.GetUtcNow();
        DateTimeOffset? until = null;
        foreach (var value in _tokens.Values)
            if (value.User == user && value.Expires > now && (until is null || value.Expires > until))
                until = value.Expires;
        return until?.UtcDateTime;
    }

    public void RevokeAll(Guid user)
    {
        foreach (var (key, value) in _tokens)
            if (value.User == user)
                _tokens.TryRemove(key, out _);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// What the keys and the orders ask for instead of the API's confirmation: a confirmation of the vault, given by it after the trading code.
/// The answer says <c>step_up_required</c> (a code is needed) or <c>trading_code_not_set</c> (this user has none yet), so the panel knows where to send them.
/// </summary>
public sealed class RequireTradingConfirmationFilter(TradingConfirmations confirmations, TradingCodeRepository codes) : IEndpointFilter
{
    public const string Header = "X-Trading-Confirmation";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var user))
            return Results.Unauthorized();

        if (confirmations.IsValid(http.Request.Headers[Header].FirstOrDefault(), user))
            return await next(context);

        var enrolled = (await codes.FindAsync(user, http.RequestAborted))?.Enrolled == true;
        return Results.Problem(
            title: "Confirmation needed",
            detail: enrolled ? "Enter your trading code to do this." : "Set up your trading code first: it is a second entry in your authenticator app, checked by the key vault itself.",
            statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["code"] = enrolled ? "step_up_required" : "trading_code_not_set" });
    }
}
