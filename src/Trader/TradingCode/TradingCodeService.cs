using Domain.Entities;
using Trader.Audit;

namespace Trader.TradingCode;

public enum CodeOutcome { Accepted, Wrong, Locked, AlreadyUsed, NotSet }

/// <summary>
/// Checks a trading code and keeps the account of the wrong ones: a right code works once, wrong ones lock (for longer each round, never for
/// good), and what matters is written to the audit log and sent to the security bot. Used for the setup (the first code proves the app has
/// the key), for the confirmation, and for the recovery codes (a wrong one counts like a wrong code).
/// </summary>
public sealed class TradingCodeService(
    TradingCodeRepository codes, ISecurityAlerts alerts, AuditWriter audit, TimeProvider time)
{
    public const string AuditActor = "system";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <param name="forSetup">True for the first code of the setup (the secret is not enrolled yet); false for a code of an enrolled user.</param>
    public async Task<CodeOutcome> VerifyAsync(Guid owner, string userName, string address, string? code, bool forSetup, CancellationToken cancellationToken)
    {
        var row = await codes.FindAsync(owner, cancellationToken);
        if (row is null || row.Enrolled == forSetup)
            return CodeOutcome.NotSet;
        if (row.LockedUntil is { } until && until > Now)
            return CodeOutcome.Locked;

        if (Totp.Match(codes.SecretOf(row), code, time.GetUtcNow()) is not { } step)
            return await FailedAsync(owner, userName, address, cancellationToken);

        // The step of a right code is taken once: the same code, or an older one, is not a second chance for whoever saw it.
        if (!await codes.TryAcceptStepAsync(owner, step, cancellationToken))
            return CodeOutcome.AlreadyUsed;

        await codes.ClearFailuresAsync(owner, cancellationToken);
        return CodeOutcome.Accepted;
    }

    /// <summary>Counts one wrong code (or recovery code): <see cref="CodeOutcome.Locked"/> when it was the one that closed the lock.</summary>
    public async Task<CodeOutcome> FailedAsync(Guid owner, string userName, string address, CancellationToken cancellationToken)
    {
        var outcome = await codes.RegisterFailureAsync(owner, Now, cancellationToken);

        if (outcome.JustLocked)
        {
            var until = outcome.LockedUntil!.Value;
            await RecordAsync(userName, $"locked the trading code after {TradingCodePolicy.Attempts} wrong codes (round {outcome.Round}) until {until:yyyy-MM-dd HH:mm} UTC", cancellationToken);
            alerts.Raise(new SecurityAlert($"trading-code-lock:{owner}", $"Trading code locked for {userName}",
                $"{TradingCodePolicy.Attempts} wrong trading codes (round {outcome.Round}), the last from {address}. No code is taken until {until:yyyy-MM-dd HH:mm} UTC. " +
                "Somebody is signed in as this user and does not have their phone: look at the sessions, and at the API."));
            return CodeOutcome.Locked;
        }

        if (outcome.Failures == TradingCodePolicy.AlertAt)
            alerts.Raise(new SecurityAlert($"trading-code-wrong:{owner}", $"Wrong trading code for {userName}",
                $"The second wrong trading code in a row, from {address}. If that was not them, somebody is signed in as them: " +
                "the trading code is what stops that from touching the keys."));

        return CodeOutcome.Wrong;
    }

    public async Task SetUpAsync(Guid owner, string userName, string address, CancellationToken cancellationToken)
    {
        await RecordAsync(userName, $"set up the trading code from {address}", cancellationToken);
        alerts.Raise(new SecurityAlert($"trading-code-set:{owner}", $"Trading code set up for {userName}",
            $"From {address}. If you did not do this, somebody else holds the code of this user now (the setup of a user who had none is the one moment " +
            "that cannot be told from the owner): reset it and look at the API."));
    }

    public async Task ResetAsync(Guid owner, string userName, string how, CancellationToken cancellationToken)
    {
        await RecordAsync(userName, $"reset the trading code ({how})", cancellationToken);
        alerts.Raise(new SecurityAlert($"trading-code-reset:{owner}", $"Trading code reset for {userName}",
            $"Reset {how}. The user sets a new one up the next time they use the keys or the orders."));
    }

    private Task RecordAsync(string userName, string details, CancellationToken cancellationToken) =>
        audit.RecordAsync(AuditAction.Updated, AuditActor, userName, details, cancellationToken, AuditEntityTypes.User);
}
