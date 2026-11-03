using Domain.Repositories;

namespace WebApi.Authentication;

/// <summary>
/// Removes the refresh tokens that ran out. Every sign-in and every renewal of a session leaves one behind (the old one is used up, not
/// deleted), and with access tokens of a few minutes that is a row every few minutes per open session, so without this the table only grows.
/// Used-up tokens that have not expired yet stay: they say that a token was already used.
/// </summary>
public sealed class RefreshTokenCleanupService(IServiceScopeFactory scopes, ILogger<RefreshTokenCleanupService> logger) : BackgroundService
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not worth stopping the API for: it tries again in six hours.
                logger.LogWarning(ex, "Could not remove the expired refresh tokens.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
