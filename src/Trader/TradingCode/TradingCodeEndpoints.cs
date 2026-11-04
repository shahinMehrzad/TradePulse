using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Trader.Security;

namespace Trader.TradingCode;

/// <param name="Enrolled">False until the user proved the app has the code.</param>
/// <param name="LockedUntil">When wrong codes keep this user from confirming (UTC); null when nothing does.</param>
public sealed record TradingCodeStatus(bool Enrolled, DateTime? LockedUntil, int RecoveryCodesLeft, DateTime? ConfirmedUntil);

public sealed record TradingCodeSetup(string SharedKey, string AuthenticatorUri);

public sealed record CodeRequest(string? Code);

public sealed record RecoveryRequest(string? RecoveryCode);

/// <summary>The recovery codes are in this answer and nowhere else; the vault keeps only their hashes.</summary>
public sealed record SetUpResult(IReadOnlyList<string> RecoveryCodes, string ConfirmationToken, DateTime ExpiresAt);

public sealed record ConfirmationResult(string Token, DateTime ExpiresAt);

/// <summary>
/// The trading code: a second entry in the authenticator app whose secret only the vault has (it is made here and goes from here to the panel,
/// never through the API). A confirmation of the vault, given after this code, is what the keys and the orders ask for, so that a hacked API,
/// which can make a sign-in for anybody, still cannot move a key or place an order.
/// </summary>
public static class TradingCodeEndpoints
{
    private const string RecoveryAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static IEndpointRouteBuilder MapTradingCode(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/trading-code").RequireAuthorization(TokenSecurity.AdminPolicy);

        group.MapGet("", Status);
        group.MapPost("begin", Begin).RequireRateLimiting("keys");
        group.MapPost("setup", SetUp).RequireRateLimiting("keys");
        group.MapPost("confirmation", Confirm).RequireRateLimiting("keys");
        group.MapPost("reset", Reset).RequireRateLimiting("keys");
        return app;
    }

    private static Guid OwnerOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static string NameOf(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name) ?? "unknown";

    private static string AddressOf(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static async Task<IResult> Status(
        ClaimsPrincipal user, TradingCodeRepository codes, TradingConfirmations confirmations, TimeProvider time, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(user);
        var row = await codes.FindAsync(owner, cancellationToken);
        var lockedUntil = row?.LockedUntil is { } until && until > time.GetUtcNow().UtcDateTime ? until : (DateTime?)null;
        return Results.Ok(new TradingCodeStatus(
            row?.Enrolled == true, lockedUntil, row?.Enrolled == true ? await codes.RecoveryCodesLeftAsync(owner, cancellationToken) : 0, confirmations.ConfirmedUntil(owner)));
    }

    /// <summary>The key for the app (the same one again until the setup is finished), when the user has no code yet.</summary>
    private static async Task<IResult> Begin(ClaimsPrincipal user, TradingCodeRepository codes, TimeProvider time, CancellationToken cancellationToken)
    {
        var begun = await codes.BeginAsync(OwnerOf(user), time.GetUtcNow().UtcDateTime, cancellationToken);
        if (begun is not { } started)
            return Refuse(StatusCodes.Status409Conflict, "Already set up", "This user has a trading code already.");

        return Results.Ok(new TradingCodeSetup(Totp.ToBase32(started.Secret), Totp.Uri(NameOf(user), started.Secret)));
    }

    /// <summary>The first code proves that the app has the key. The user is then enrolled, gets the recovery codes (once) and a confirmation.</summary>
    private static async Task<IResult> SetUp(
        CodeRequest request, ClaimsPrincipal user, HttpContext http, TradingCodeRepository codes, TradingCodeService service, TradingConfirmations confirmations,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(user);
        var outcome = await service.VerifyAsync(owner, NameOf(user), AddressOf(http), request.Code, forSetup: true, cancellationToken);
        if (outcome is not CodeOutcome.Accepted)
            return Answer(outcome, alreadySetUp: (await codes.FindAsync(owner, cancellationToken))?.Enrolled == true);

        var plain = Enumerable.Range(0, TradingCodePolicy.RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        await codes.CompleteEnrollmentAsync(owner, plain.Select(HashRecoveryCode).ToList(), time.GetUtcNow().UtcDateTime, cancellationToken);
        await service.SetUpAsync(owner, NameOf(user), AddressOf(http), cancellationToken);

        var (token, expires) = confirmations.Issue(owner);
        return Results.Ok(new SetUpResult(plain, token, expires));
    }

    private static async Task<IResult> Confirm(
        CodeRequest request, ClaimsPrincipal user, HttpContext http, TradingCodeService service, TradingConfirmations confirmations, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(user);
        var outcome = await service.VerifyAsync(owner, NameOf(user), AddressOf(http), request.Code, forSetup: false, cancellationToken);
        if (outcome is not CodeOutcome.Accepted)
            return Answer(outcome, alreadySetUp: false);

        var (token, expires) = confirmations.Issue(owner);
        return Results.Ok(new ConfirmationResult(token, expires));
    }

    /// <summary>
    /// A user who lost the phone uses one of the recovery codes (shown once at the setup) to start again. Nobody else can: not an administrator,
    /// not the API. The last way is the vault's own command on the server (<c>reset-trading-code</c>).
    /// </summary>
    private static async Task<IResult> Reset(
        RecoveryRequest request, ClaimsPrincipal user, HttpContext http, TradingCodeRepository codes, TradingCodeService service, TradingConfirmations confirmations,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(user);
        var row = await codes.FindAsync(owner, cancellationToken);
        if (row is not { Enrolled: true })
            return Refuse(StatusCodes.Status409Conflict, "No trading code", "There is no trading code to reset.");
        if (row.LockedUntil is { } until && until > time.GetUtcNow().UtcDateTime)
            return Refuse(StatusCodes.Status409Conflict, "Locked", "Too many wrong codes. Try again later.");

        var normalized = NormalizeRecoveryCode(request.RecoveryCode);
        if (normalized.Length == 0 || !await codes.ConsumeRecoveryCodeAsync(owner, HashRecoveryCode(normalized), cancellationToken))
        {
            var failed = await service.FailedAsync(owner, NameOf(user), AddressOf(http), cancellationToken);
            return failed == CodeOutcome.Locked
                ? Refuse(StatusCodes.Status409Conflict, "Locked", "Too many wrong codes. Try again later.")
                : Refuse(StatusCodes.Status400BadRequest, "Invalid recovery code", "That recovery code is not valid (or was used already).");
        }

        await codes.DeleteAsync(owner, cancellationToken);
        confirmations.RevokeAll(owner);
        await service.ResetAsync(owner, NameOf(user), $"with a recovery code, from {AddressOf(http)}", cancellationToken);
        return Results.NoContent();
    }

    private static IResult Answer(CodeOutcome outcome, bool alreadySetUp) => outcome switch
    {
        CodeOutcome.Locked => Refuse(StatusCodes.Status409Conflict, "Locked", "Too many wrong codes. Try again later."),
        CodeOutcome.AlreadyUsed => Refuse(StatusCodes.Status400BadRequest, "Code already used", "That code was already used. Wait for the next one (it changes every 30 seconds)."),
        CodeOutcome.NotSet when alreadySetUp => Refuse(StatusCodes.Status409Conflict, "Already set up", "This user has a trading code already."),
        CodeOutcome.NotSet => Refuse(StatusCodes.Status409Conflict, "No trading code", "Set up the trading code first."),
        _ => Refuse(StatusCodes.Status400BadRequest, "Invalid code", "The code is incorrect. Check the app and try the next code.")
    };

    private static IResult Refuse(int status, string title, string detail) => Results.Problem(title: title, detail: detail, statusCode: status);

    // ---- recovery codes: ten characters in two groups, no look-alikes (0/O, 1/I), kept only as hashes

    private static string NewRecoveryCode()
    {
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
    }

    private static string NormalizeRecoveryCode(string? code) =>
        new((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRecoveryCode(code))));
}
