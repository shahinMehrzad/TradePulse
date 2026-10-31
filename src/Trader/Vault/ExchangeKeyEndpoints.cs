using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Domain.Entities;
using Trader.Audit;
using Trader.Binance;
using Trader.Security;

namespace Trader.Vault;

/// <summary>What a user sees of a key. The private key is not in it, and cannot be: nothing in this program hands it out.</summary>
public sealed record ExchangeKeyView(
    Guid Id, string Label, string Exchange, string Algorithm, string Status, string PublicKeyPem,
    string? ApiKeyHint, IReadOnlyList<string> Problems, DateTime CreatedAt, DateTime? VerifiedAt, string Environment);

/// <param name="Environment"><c>Live</c> (the default) or <c>Demo</c>; fixed for the life of the key.</param>
public sealed record CreateKeyRequest(string? Label, string? Environment = null);

public sealed record ApiKeyRequest(string? ApiKey);

public static partial class ExchangeKeyEndpoints
{
    public const string Exchange = "binance-futures";

    /// <summary>Binance API keys are 64 letters and digits; a wide range is allowed so a change on their side does not lock anyone out.</summary>
    [GeneratedRegex("^[A-Za-z0-9]{16,128}$")]
    private static partial Regex ApiKeyPattern();

    public static IEndpointRouteBuilder MapExchangeKeys(this IEndpointRouteBuilder app)
    {
        // Every route is for an administrator, and every key a route touches is the signed-in user's own: a key of somebody else is a 404.
        var keys = app.MapGroup("/keys").RequireAuthorization(TokenSecurity.AdminPolicy);

        keys.MapGet("", List);
        keys.MapPost("", Create).AddEndpointFilter<RequireElevationFilter>().RequireRateLimiting("keys");
        keys.MapPut("{id:guid}/api-key", SetApiKey).AddEndpointFilter<RequireElevationFilter>().RequireRateLimiting("keys");
        keys.MapPost("{id:guid}/verify", Verify).AddEndpointFilter<RequireElevationFilter>().RequireRateLimiting("keys");
        keys.MapDelete("{id:guid}", Delete).AddEndpointFilter<RequireElevationFilter>();
        return app;
    }

    private static Guid OwnerOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static string NameOf(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name) ?? "unknown";

    private static async Task<IResult> List(ClaimsPrincipal user, ExchangeKeyRepository repository, CancellationToken cancellationToken) =>
        Results.Ok((await repository.ListAsync(OwnerOf(user), cancellationToken)).Select(View));

    private static async Task<IResult> Create(
        CreateKeyRequest request, ClaimsPrincipal user, ExchangeKeyRepository repository, VaultCrypto crypto, AuditWriter audit,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var label = request.Label?.Trim() ?? "";
        if (label.Length is 0 or > 50)
            return Results.Problem(title: "Invalid label", detail: "Give the key a name of 1 to 50 characters.", statusCode: StatusCodes.Status400BadRequest);

        KeyEnvironment environment = KeyEnvironment.Live;
        if (!string.IsNullOrWhiteSpace(request.Environment) && !Enum.TryParse(request.Environment.Trim(), ignoreCase: true, out environment) | !Enum.IsDefined(environment))
            return Results.Problem(title: "Invalid environment", detail: "The environment is Live or Demo.", statusCode: StatusCodes.Status400BadRequest);

        var owner = OwnerOf(user);
        if (await repository.CountAsync(owner, cancellationToken) >= ExchangeKeyRepository.MaxKeysPerUser)
            return Results.Problem(title: "Too many keys", detail: $"At most {ExchangeKeyRepository.MaxKeysPerUser} keys. Delete one you do not use.", statusCode: StatusCodes.Status409Conflict);

        var pair = KeyPairFactory.Create(KeyAlgorithm.Rsa);
        var id = Guid.NewGuid();
        try
        {
            await repository.AddAsync(new ExchangeKeyRow(
                id, owner, label, Exchange, KeyAlgorithm.Rsa.ToString(), pair.PublicKeyPem,
                crypto.Encrypt(pair.PrivateKeyPkcs8, id, owner), ApiKey: null, KeyStatus.PendingApiKey, Problems: null,
                VerifiedAt: null, time.GetUtcNow().UtcDateTime, environment.ToString()), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pair.PrivateKeyPkcs8);
        }

        await audit.RecordAsync(AuditAction.Added, NameOf(user), label, $"made a {environment} key pair for Binance Futures (the private key stays in the vault)", cancellationToken);
        return Results.Created($"/keys/{id}", View((await repository.FindAsync(owner, id, cancellationToken))!));
    }

    private static async Task<IResult> SetApiKey(
        Guid id, ApiKeyRequest request, ClaimsPrincipal user, ExchangeKeyRepository repository, VaultCrypto crypto, KeyVerifier verifier,
        AuditWriter audit, TimeProvider time, CancellationToken cancellationToken)
    {
        var apiKey = request.ApiKey?.Trim() ?? "";
        if (!ApiKeyPattern().IsMatch(apiKey))
            return Results.Problem(title: "Invalid API key", detail: "That does not look like an API key from Binance (letters and digits only). Do not paste the public key here.", statusCode: StatusCodes.Status400BadRequest);

        var owner = OwnerOf(user);
        if (await repository.FindAsync(owner, id, cancellationToken) is null)
            return NotFound();

        await repository.SetApiKeyAsync(owner, id, apiKey, cancellationToken);
        return await VerifyAndSaveAsync(owner, id, NameOf(user), repository, crypto, verifier, audit, time, "attached the API key", cancellationToken);
    }

    private static async Task<IResult> Verify(
        Guid id, ClaimsPrincipal user, ExchangeKeyRepository repository, VaultCrypto crypto, KeyVerifier verifier, AuditWriter audit,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(user);
        var row = await repository.FindAsync(owner, id, cancellationToken);
        if (row is null)
            return NotFound();
        if (row.ApiKey is null)
            return Results.Problem(title: "No API key yet", detail: "Give the API key that Binance made for the public key first.", statusCode: StatusCodes.Status409Conflict);

        return await VerifyAndSaveAsync(owner, id, NameOf(user), repository, crypto, verifier, audit, time, "checked the key against Binance", cancellationToken);
    }

    private static async Task<IResult> VerifyAndSaveAsync(
        Guid owner, Guid id, string userName, ExchangeKeyRepository repository, VaultCrypto crypto, KeyVerifier verifier, AuditWriter audit,
        TimeProvider time, string what, CancellationToken cancellationToken)
    {
        var row = (await repository.FindAsync(owner, id, cancellationToken))!;

        VerificationResult result;
        var privateKey = crypto.Decrypt(row.PrivateKeyEncrypted, row.Id, row.OwnerUserId);
        try
        {
            using var signer = SignerFactory.Create(Enum.Parse<KeyAlgorithm>(row.Algorithm), privateKey);
            result = await verifier.VerifyAsync(signer, row.ApiKey!, Enum.Parse<KeyEnvironment>(row.Environment), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }

        await repository.SetVerificationAsync(owner, id, result.Accepted ? KeyStatus.Active : KeyStatus.Rejected, result.Problems, time.GetUtcNow().UtcDateTime, cancellationToken);
        await audit.RecordAsync(AuditAction.Updated, userName, row.Label,
            $"{what}: {(result.Accepted ? "accepted" : $"rejected ({result.Problems.Count} problem(s))")}", cancellationToken);
        return Results.Ok(View((await repository.FindAsync(owner, id, cancellationToken))!));
    }

    private static async Task<IResult> Delete(Guid id, ClaimsPrincipal user, ExchangeKeyRepository repository, AuditWriter audit, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(user);
        var row = await repository.FindAsync(owner, id, cancellationToken);
        if (row is null || !await repository.DeleteAsync(owner, id, cancellationToken))
            return NotFound();

        await audit.RecordAsync(AuditAction.Removed, NameOf(user), row.Label, "deleted the key (the private key is gone; delete the API key at Binance too)", cancellationToken);
        return Results.NoContent();
    }

    private static IResult NotFound() =>
        Results.Problem(title: "Key not found", detail: "That key does not exist (anymore).", statusCode: StatusCodes.Status404NotFound);

    private static ExchangeKeyView View(ExchangeKeyRow row) => new(
        row.Id, row.Label, row.Exchange, row.Algorithm, row.Status, row.PublicKeyPem,
        row.ApiKey is { Length: > 4 } key ? "…" + key[^4..] : null,
        row.Problems?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [],
        row.CreatedAt, row.VerifiedAt, row.Environment);
}
