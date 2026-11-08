using System.Text;
using Domain.Entities;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Signals;

/// <summary>What came of a request carrying a signal. Every case is one answer of the webhook.</summary>
public abstract record IntakeResult
{
    private IntakeResult() { }

    /// <summary>The signal was taken and a paper trade is open.</summary>
    public sealed record Accepted(string SignalId, int TradeId) : IntakeResult;

    /// <summary>This signal was received before and taken: nothing was done again.</summary>
    public sealed record Duplicate(string SignalId) : IntakeResult;

    /// <summary>The signal is not taken, and sending it again will not change that.</summary>
    public sealed record Rejected(string SignalId, SignalRejectionEnum Reason) : IntakeResult;

    /// <summary>The request is not what the contract says. Nothing was stored.</summary>
    public sealed record Malformed(string Error) : IntakeResult;

    /// <summary>The request is not from the sender (no or wrong signature, or a time too far from ours). Nothing was read of it.</summary>
    public sealed record Unauthorized : IntakeResult;

    /// <summary>There is no recent price of the symbol to judge the signal by. Nothing was stored: try again.</summary>
    public sealed record NoPrice(string SignalId) : IntakeResult;

    /// <summary>Something of ours is down (the database). Nothing was stored: try again.</summary>
    public sealed record Unavailable : IntakeResult;
}

/// <summary>
/// Takes a signal from the program that makes them: proves who sent it, reads it, judges it, and when it is worth taking opens a paper trade,
/// hands it to the price watch and to the chat, and answers. The whole decision is here and every part of it is a plain call, so each step is
/// tested with plain numbers.
/// <para>
/// A signal is judged once. Every signal that gets as far as being judged is stored with its verdict, the refused ones too, and sending it
/// again gets the same answer: a taken signal and its trade are stored together or not at all, so there is never a taken signal without a trade.
/// </para>
/// </summary>
public sealed class SignalIntake(
    SignalAuthenticator authenticator,
    ISignalStore store,
    ISignalStrategyConfigurationRepository strategies,
    IFollowedSymbolSource symbols,
    PriceBoard board,
    ITradeTracker tracker,
    IOpenedTradeSink announcer,
    SignalIntakeOptions options,
    TimeProvider time,
    ILogger<SignalIntake> logger)
{
    public async Task<IntakeResult> TakeAsync(string body, string? timestamp, string? signature, string? idempotencyKey, CancellationToken cancellationToken)
    {
        // Nothing of the body is read, and nothing is said about why, before the sender is proven.
        if (Encoding.UTF8.GetByteCount(body) > options.MaxBodyBytes)
            return authenticator.IsAuthentic(timestamp, signature, body) ? new IntakeResult.Malformed("The body is too large.") : new IntakeResult.Unauthorized();

        if (!authenticator.IsAuthentic(timestamp, signature, body))
            return new IntakeResult.Unauthorized();

        if (!SignalParser.TryParse(body, out var signal, out var error))
            return new IntakeResult.Malformed(error);

        if (idempotencyKey != signal.SignalId)
            return new IntakeResult.Malformed("Idempotency-Key must be the signalId.");

        try
        {
            return await DecideAsync(signal, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A database that is down is the sender's to retry; nothing was stored, or (for the one step that stores) all of it was.
            logger.LogError("The signal {SignalId} could not be taken ({Error})", signal.SignalId, ex.GetType().Name);
            return new IntakeResult.Unavailable();
        }
    }

    private async Task<IntakeResult> DecideAsync(TradeSignal signal, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;

        if (await store.FindSignalAsync(signal.SignalId, cancellationToken) is { } known)
            return Same(known);

        var settings = await strategies.GetByStrategy(signal.Strategy, cancellationToken);
        if (settings is null)
            return await RefuseAsync(signal, SignalRejectionEnum.UnknownStrategy, now, cancellationToken);

        var listed = (await symbols.GetAsync(cancellationToken)).Any(s => s.Exchange == signal.Exchange && s.Market == signal.Market && s.Symbol == signal.Symbol);
        if (!listed)
            return await RefuseAsync(signal, SignalRejectionEnum.UnknownSymbol, now, cancellationToken);

        if (board.Get(signal.Exchange, signal.Symbol, now, options.MaxPriceAge) is not { } price)
        {
            logger.LogWarning("The signal {SignalId} cannot be judged: no price of {Symbol} newer than {Seconds:0} s", signal.SignalId, signal.Symbol, options.MaxPriceAge.TotalSeconds);
            return new IntakeResult.NoPrice(signal.SignalId);
        }

        var open = await store.CountOpenAsync(signal.Exchange, signal.Strategy, signal.Symbol, cancellationToken);

        if (SignalValidator.Validate(signal, settings.ToSettings(), price.Price, now, open) is { } rejection)
            return await RefuseAsync(signal, rejection, now, cancellationToken);

        var trade = PaperTrade.Open(signal, price.Price, now, open);
        if (!await store.RecordAsync(ReceivedSignal.From(signal, now, rejection: null), trade, cancellationToken))
            return Same(await store.FindSignalAsync(signal.SignalId, cancellationToken) ?? throw new InvalidOperationException("A signal recorded at the same time cannot be found."));

        tracker.Track(trade);
        announcer.Opened(trade);
        await LogAsync(signal, SignalEventKindEnum.Opened, $"entry {price.Price}", now, cancellationToken);

        logger.LogInformation("The signal {SignalId} was taken: {Side} {Symbol} at {Price}", signal.SignalId, signal.Side, signal.Symbol, price.Price);
        return new IntakeResult.Accepted(signal.SignalId, trade.Id);
    }

    /// <summary>The answer to a signal that was judged before: the same as the first time.</summary>
    private static IntakeResult Same(ReceivedSignal known) =>
        known.Rejection is { } reason ? new IntakeResult.Rejected(known.SignalId, reason) : new IntakeResult.Duplicate(known.SignalId);

    private async Task<IntakeResult> RefuseAsync(TradeSignal signal, SignalRejectionEnum reason, DateTime now, CancellationToken cancellationToken)
    {
        // Stored with its reason, so a sender that asks why nothing happened has an answer. A signal recorded at the same moment is the same signal.
        await store.RecordAsync(ReceivedSignal.From(signal, now, reason), null, cancellationToken);
        await LogAsync(signal, SignalEventKindEnum.Rejected, reason.ToString(), now, cancellationToken);

        logger.LogInformation("The signal {SignalId} was refused: {Reason}", signal.SignalId, reason);
        return new IntakeResult.Rejected(signal.SignalId, reason);
    }

    private async Task LogAsync(TradeSignal signal, SignalEventKindEnum kind, string detail, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            await store.AddEventAsync(new SignalEvent { SignalId = signal.SignalId, At = now, Kind = kind, Detail = detail }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The log of the signal {SignalId} could not be written ({Error})", signal.SignalId, ex.GetType().Name);
        }
    }
}
