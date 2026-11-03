using Application.Interfaces;
using Domain.Repositories;

namespace WebApi.Authentication;

/// <summary>
/// Tidies the auth tables every six hours. Refresh tokens that ran out go: every sign-in and every renewal of a session leaves one behind (the
/// old one is used up, not deleted), and with access tokens of a few minutes that is a row every few minutes per open session, so without this the
/// table only grows; used-up tokens that have not expired yet stay, they say that a token was already spent. The counts of wrong passwords and
/// codes go when no lock is in force and the last wrong attempt is more than a week old.
/// </summary>
public sealed class AuthCleanupService(IServiceScopeFactory scopes, ILogger<AuthCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>().CleanUpExpiredTokensAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<ILoginThrottle>().CleanUpAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not worth stopping the API for: it tries again in six hours.
                logger.LogWarning(ex, "Could not tidy the auth tables.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
