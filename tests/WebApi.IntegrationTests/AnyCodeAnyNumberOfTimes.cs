using Application.Interfaces;

namespace WebApi.IntegrationTests;

/// <summary>
/// The replay guard of the tests that are not about it. They sign the seeded administrator in and confirm with a code many times within the
/// same thirty seconds (one code is good for that long), which the real guard, that lets a code work once, would refuse.
/// <see cref="ReplayGuardTests"/> put the real one back.
/// </summary>
public sealed class AnyCodeAnyNumberOfTimes : ITwoFactorReplayGuard
{
    public Task<bool> TryUseAsync(Guid userId, string code, CancellationToken cancellationToken) => Task.FromResult(true);
}
