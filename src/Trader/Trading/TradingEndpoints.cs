using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Domain.Entities;
using Microsoft.Extensions.Options;
using Trader.Audit;
using Trader.Binance;
using Trader.Security;
using Trader.Vault;

namespace Trader.Trading;

public sealed record PlaceOrderRequest(Guid KeyId, string? Symbol, string? Side, decimal Quantity, bool ReduceOnly = false);

public sealed record OrderView(
    Guid Id, Guid KeyId, string Symbol, string Side, decimal Quantity, bool ReduceOnly, decimal NotionalUsd, string Status,
    long? ExchangeOrderId, string? Error, DateTime CreatedAt);

public sealed record TradingStateView(
    bool Halted, bool HaltedByServer, bool LiveAllowed, IReadOnlyList<string> AllowedSymbols, decimal MaxOrderUsd, decimal MaxDailyUsd, decimal OpenedTodayUsd);

/// <summary>
/// Market orders for the signed-in user's own keys, behind the limits in <see cref="TradingOptions"/> and a halt switch. Anything that
/// <b>opens</b> exposure is checked against the symbol list, the size caps and the halt; an order that only <b>reduces</b> a position
/// (<c>ReduceOnly</c>) is allowed while halted and is not counted against the caps, because the way out must never be blocked.
/// </summary>
public static class TradingEndpoints
{
    private const int MaxQuantityDecimals = 8;

    public static IEndpointRouteBuilder MapTrading(this IEndpointRouteBuilder app)
    {
        var trading = app.MapGroup("/trading").RequireAuthorization(TokenSecurity.AdminPolicy);

        trading.MapGet("state", State);
        trading.MapGet("orders", Orders);
        trading.MapPost("orders", Place).AddEndpointFilter<RequireElevationFilter>().RequireRateLimiting("orders");
        // Stopping is one click with no extra code on purpose (an emergency); starting again asks for the second factor.
        trading.MapPost("halt", Halt);
        trading.MapPost("resume", Resume).AddEndpointFilter<RequireElevationFilter>();
        return app;
    }

    private static Guid OwnerOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static string NameOf(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name) ?? "unknown";

    private static DateTime DayStart(TimeProvider time) => time.GetUtcNow().UtcDateTime.Date;

    private static async Task<IResult> State(
        ClaimsPrincipal user, TradingRepository repository, IOptions<TradingOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(user);
        var settings = options.Value;
        return Results.Ok(new TradingStateView(
            settings.Halted || await repository.IsHaltedAsync(owner, cancellationToken), settings.Halted, settings.AllowLive, settings.AllowedSymbols,
            settings.MaxOrderUsd, settings.MaxDailyUsd, await repository.OpenedSinceAsync(owner, DayStart(time), cancellationToken)));
    }

    private static async Task<IResult> Orders(ClaimsPrincipal user, TradingRepository repository, CancellationToken cancellationToken) =>
        Results.Ok((await repository.RecentAsync(OwnerOf(user), 50, cancellationToken)).Select(View));

    private static async Task<IResult> Halt(ClaimsPrincipal user, TradingRepository repository, AuditWriter audit, TimeProvider time, CancellationToken cancellationToken)
    {
        await repository.SetHaltedAsync(OwnerOf(user), halted: true, time.GetUtcNow().UtcDateTime, cancellationToken);
        await audit.RecordAsync(AuditAction.Updated, NameOf(user), "trading", "halted: no new positions can be opened", cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> Resume(ClaimsPrincipal user, TradingRepository repository, AuditWriter audit, TimeProvider time, CancellationToken cancellationToken)
    {
        await repository.SetHaltedAsync(OwnerOf(user), halted: false, time.GetUtcNow().UtcDateTime, cancellationToken);
        await audit.RecordAsync(AuditAction.Updated, NameOf(user), "trading", "resumed", cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> Place(
        PlaceOrderRequest request, ClaimsPrincipal user, ExchangeKeyRepository keys, TradingRepository repository, VaultCrypto crypto,
        BinanceClient binance, IOptions<BinanceOptions> binanceOptions, IOptions<TradingOptions> tradingOptions, AuditWriter audit,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var settings = tradingOptions.Value;
        var owner = OwnerOf(user);

        var symbol = request.Symbol?.Trim().ToUpperInvariant() ?? "";
        var side = request.Side?.Trim().ToUpperInvariant() ?? "";
        if (!settings.AllowedSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            return Refuse(StatusCodes.Status422UnprocessableEntity, "Symbol not allowed", $"Only {string.Join(", ", settings.AllowedSymbols)} can be traded.");
        if (side is not ("BUY" or "SELL"))
            return Refuse(StatusCodes.Status400BadRequest, "Invalid side", "The side is BUY or SELL.");
        if (request.Quantity <= 0 || request.Quantity > 1_000_000m || Math.Round(request.Quantity, MaxQuantityDecimals) != request.Quantity)
            return Refuse(StatusCodes.Status400BadRequest, "Invalid quantity", $"The quantity is more than 0, with at most {MaxQuantityDecimals} decimals.");

        var key = await keys.FindAsync(owner, request.KeyId, cancellationToken);
        if (key is null)
            return Refuse(StatusCodes.Status404NotFound, "Key not found", "That key does not exist (anymore).");
        if (key.Status != KeyStatus.Active || key.ApiKey is null)
            return Refuse(StatusCodes.Status409Conflict, "Key not active", "Only a key that Binance has accepted (status Active) can trade.");

        var environment = Enum.Parse<KeyEnvironment>(key.Environment);
        if (environment == KeyEnvironment.Live && !settings.AllowLive)
            return Refuse(StatusCodes.Status409Conflict, "Live trading is off", "This server only trades with Demo keys. The owner of the server turns live trading on (Trading__AllowLive).");

        if (!request.ReduceOnly && (settings.Halted || await repository.IsHaltedAsync(owner, cancellationToken)))
            return Refuse(StatusCodes.Status409Conflict, "Trading is halted", "New positions are blocked. Orders that only reduce a position still go through; resume trading to open new ones.");

        // The size of the order in USDT needs a price. Without one the caps cannot be checked, so the order is not sent.
        var binanceSettings = binanceOptions.Value;
        var price = await MarkPriceAsync(binance, binanceSettings.FuturesUrl(environment), symbol, cancellationToken);
        if (price is null)
            return Refuse(StatusCodes.Status502BadGateway, "No price", "Could not read the current price from Binance, so the size of the order cannot be checked. Try again.");

        var notional = Math.Round(request.Quantity * price.Value, 2);
        if (!request.ReduceOnly && notional > settings.MaxOrderUsd)
            return Refuse(StatusCodes.Status422UnprocessableEntity, "Order too large", $"This order is about {notional} USDT; the limit for one order is {settings.MaxOrderUsd} USDT.");

        var now = time.GetUtcNow().UtcDateTime;
        var order = new OrderRow(
            Guid.NewGuid(), owner, key.Id, symbol, side, request.Quantity, request.ReduceOnly, notional, OrderStatus.Pending,
            ExchangeOrderId: null, Error: null, now);
        if (!await repository.TryReserveAsync(order, settings.MaxDailyUsd, DayStart(time), cancellationToken))
            return Refuse(StatusCodes.Status422UnprocessableEntity, "Daily limit reached", $"This order would take today's total over {settings.MaxDailyUsd} USDT.");

        var parameters = string.Create(CultureInfo.InvariantCulture,
            $"symbol={symbol}&side={side}&type=MARKET&quantity={request.Quantity}&newClientOrderId={order.Id:N}{(request.ReduceOnly ? "&reduceOnly=true" : "")}");

        BinanceReply reply;
        var privateKey = crypto.Decrypt(key.PrivateKeyEncrypted, key.Id, key.OwnerUserId);
        try
        {
            using var signer = SignerFactory.Create(Enum.Parse<KeyAlgorithm>(key.Algorithm), privateKey);
            reply = await binance.PostSignedAsync(binanceSettings.FuturesUrl(environment), "/fapi/v1/order", parameters, key.ApiKey, signer, CancellationToken.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }

        // 4xx: Binance looked at it and said no. No answer or a 5xx: it may have gone through, so it keeps counting until someone looks.
        var (status, exchangeOrderId, error) = reply switch
        {
            { Ok: true } => (OrderStatus.Placed, ExchangeOrderIdOf(reply), (string?)null),
            { Status: >= 400 and < 500 } => (OrderStatus.Failed, null, Describe(reply)),
            _ => (OrderStatus.Unknown, null, Describe(reply))
        };
        await repository.CompleteAsync(order.Id, status, exchangeOrderId, error, CancellationToken.None);
        await audit.RecordAsync(AuditAction.Added, NameOf(user), $"{symbol} {side} {request.Quantity}",
            $"{environment} market order{(request.ReduceOnly ? " (reduce only)" : "")} of about {notional} USDT: {status}", CancellationToken.None);

        return Results.Ok(View(order with { Status = status, ExchangeOrderId = exchangeOrderId, Error = error }));
    }

    private static async Task<decimal?> MarkPriceAsync(BinanceClient binance, string baseUrl, string symbol, CancellationToken cancellationToken)
    {
        var reply = await binance.GetPublicAsync(baseUrl, $"/fapi/v1/premiumIndex?symbol={symbol}", cancellationToken);
        return reply is { Ok: true, Body: { ValueKind: JsonValueKind.Object } body }
               && body.TryGetProperty("markPrice", out var mark)
               && decimal.TryParse(mark.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
               && price > 0
            ? price
            : null;
    }

    private static long? ExchangeOrderIdOf(BinanceReply reply) =>
        reply.Body is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("orderId", out var id) && id.TryGetInt64(out var value) ? value : null;

    private static string Describe(BinanceReply reply) => reply switch
    {
        { Status: 0 } => "No answer from Binance. The order may or may not exist there: check the position.",
        { Code: { } code, Message: { } message } => $"Binance error {code}: {(message.Length > 150 ? message[..150] : message)}",
        _ => $"Binance answered with HTTP {reply.Status}."
    };

    private static IResult Refuse(int status, string title, string detail) => Results.Problem(title: title, detail: detail, statusCode: status);

    private static OrderView View(OrderRow row) => new(
        row.Id, row.KeyId, row.Symbol, row.Side, row.Quantity, row.ReduceOnly, row.NotionalUsd, row.Status, row.ExchangeOrderId, row.Error, row.CreatedAt);
}
