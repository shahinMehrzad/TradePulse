namespace Application.Interfaces;

/// <summary>Lets a two-factor code work once. The authenticator and email codes of Identity can be used again for as long as they are valid.</summary>
public interface ITwoFactorReplayGuard
{
    /// <returns>True the first time this user presents this code; false when it was already used (it is still good, but spent).</returns>
    Task<bool> TryUseAsync(Guid userId, string code, CancellationToken cancellationToken);
}
