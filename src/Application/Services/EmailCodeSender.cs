using Application.Interfaces;
using Application.Notifications;
using Domain.Exceptions;

namespace Application.Services;

/// <summary>Mails a sign-in code to a user, within the limits of <see cref="EmailCodeThrottle"/>. The code is never logged.</summary>
public sealed class EmailCodeSender(IIdentityService identity, IMailSender mail, EmailCodeThrottle throttle)
{
    public async Task SendAsync(Guid userId, bool forEnabling, CancellationToken cancellationToken)
    {
        if (!mail.IsConfigured)
            throw new ConfigurationConflictException("Email is not set up on this server.");

        // The limit comes first, so asking over and over cannot be used to find out who has email codes on.
        if (!throttle.TryAcquire(userId, forEnabling))
            throw new ConfigurationConflictException("A code was sent a moment ago. Wait a minute before asking for another one.");

        var code = await identity.CreateEmailCodeAsync(userId, forEnabling, cancellationToken)
                   ?? throw new ConfigurationConflictException("Email codes are not available for this account.");

        try
        {
            await mail.SendAsync(code.Address, "Your TradePulse sign-in code",
                $"Your sign-in code is {code.Code}\n\nIt works for a few minutes. If you did not ask for it, somebody knows your password: change it.",
                cancellationToken);
        }
        catch (NotificationException)
        {
            throw new ConfigurationConflictException("The email could not be sent. Try again later, or use your authenticator app.");
        }
    }
}
